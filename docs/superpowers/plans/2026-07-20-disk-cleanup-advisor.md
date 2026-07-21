# DiskCleanupAdvisor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a cross-platform F# script that finds disposable files on a developer machine, explains why each is disposable, and — only on explicit request — moves them to the platform Trash.

**Architecture:** A chain of `.fsx` modules composed by a single entry script via `#load`. Pure logic (rule matching, risk gates, glob matching, duplicate grouping) is separated from I/O behind a `FileSystemOps` record of functions, so tests run against an in-memory tree. Deletion is guarded by three independent gates and has no `unlink` code path — only Trash relocation.

**Tech Stack:** F# 9, .NET 10 (`dotnet fsi`), base class library only. No NuGet packages, no build step.

**Spec:** [2026-07-20-disk-cleanup-advisor-design.md](../specs/2026-07-20-disk-cleanup-advisor-design.md)

## Global Constraints

These apply to **every task**. Each task's requirements implicitly include this section.

- **F# 9 / .NET 10.** Verified on the dev machine: `dotnet --version` → `10.0.302`.
- **No third-party libraries.** Base class library only. No `#r "nuget: ..."` anywhere.
- **No build step.** Everything runs via `dotnet fsi`. No `.fsproj`.
- **File size:** 150–400 lines per module, 800 absolute maximum. Split if exceeded.
- **Every public function has an XML doc comment** (`/// <summary>`). Every module begins with a `(** ... *)` module-level doc block.
- **Immutable data.** Records and DUs. `mutable` only inside a function body where profiling justifies it, with a comment stating why.
- **`Result<'T,'Error>` for all fallible operations.** No exceptions for control flow. Exceptions from BCL calls are caught at the boundary and converted to `Result`.
- **Pipeline operators** (`|>`) for data flow. Active patterns where they clarify matching.
- **Commit after every task** using Conventional Commits (`feat:`, `test:`, `fix:`, `docs:`, `refactor:`).
- **No AI attribution trailers in commit messages.** No `Co-Authored-By: Claude`, no `Generated with`, no 🤖 lines. This is a hard project rule.
- **`#load` order is a topologically sorted DAG.** A module may only `#load` modules defined before it in the graph. `#load` is idempotent in FSI — loading the same file twice is safe.

### Verified environment facts

These were empirically confirmed before writing this plan. Do not re-derive them.

| Fact | Result |
|---|---|
| `#load` composition across `.fsx` files | Works. `dotnet fsi entry.fsx` resolves relative `#load` paths from the loading file's directory. |
| Script arguments | Use **`fsi.CommandLineArgs`** → `[| "script.fsx"; "--scan"; ... |]`. Do **NOT** use `Environment.GetCommandLineArgs()` — it includes the `fsi.dll` path and script name as separate entries. Skip element 0. |
| Private record constructors in a module | Works. `type T = private { ... }` blocks construction outside the declaring module. |
| Symlink detection | `FileInfo(p).LinkTarget` is non-null for a link, **null** for a real file. Also works on `DirectoryInfo`. |
| Unix mode | `File.GetUnixFileMode path` → `UnixFileMode` flags enum. |
| `Channels` + `Parallel.ForEachAsync` | Works. `Parallel.ForEachAsync(ch.Reader.ReadAllAsync(), opts, fn)`. |
| **`System.Text.Json` on F# DUs** | **THROWS `NotSupportedException`.** A DTO layer with primitive fields is mandatory — see Task 9. Records of primitives serialize and round-trip fine. |
| macOS Trash | `~/.Trash` exists. |
| Time Machine snapshots | `tmutil listlocalsnapshots /` works and this machine has an active snapshot. |
| Device ID for filesystem boundary | `stat -f '%d'` on macOS. In .NET, compare via `DriveInfo` on the resolved root. |

---

## File Structure

| File | Responsibility | Approx lines |
|---|---|---|
| `tests/TestFramework.fsx` | Minimal assert/run harness, exit code | 90 |
| `src/Types.fsx` | All DUs and records. No logic. | 200 |
| `src/Utilities.fsx` | Byte/duration formatting, path normalization, `Result` combinators, glob matching | 280 |
| `src/Logging.fsx` | Leveled structured logger writing to stderr | 120 |
| `src/Diagnostics.fsx` | Counters, stopwatch, throughput, memory | 150 |
| `src/Platform.fsx` | OS detection, known folders, elevation check, `FileSystemOps` real impl | 300 |
| `src/Configuration.fsx` | JSON config load, merge, validation | 250 |
| `src/Rules.fsx` | Rule catalog — macOS full, Linux/Windows seeded | 400 |
| `src/RiskEngine.fsx` | Three gates, denylist veto, override clamping | 300 |
| `src/FileClassifier.fsx` | Extension/name patterns → `Category` | 150 |
| `src/DirectoryWalker.fsx` | Bounded-parallel traversal, error collection | 300 |
| `src/Scanner.fsx` | Walker + rules + classifier → `ScanResult` | 300 |
| `src/Hashing.fsx` | SHA-256, MD5, sampled fast-hash | 150 |
| `src/Duplicates.fsx` | Three-stage duplicate funnel | 200 |
| `src/Cleaner.fsx` | Trash relocation, deletion journal | 300 |
| `src/Report/Dto.fsx` | DU→primitive DTO projection (JSON safety layer) | 150 |
| `src/Report/Json.fsx` | `System.Text.Json` serialization | 80 |
| `src/Report/Console.fsx` | ANSI-colored console rendering | 250 |
| `src/Report/Markdown.fsx` | Markdown rendering | 200 |
| `src/Report/Html.fsx` | Self-contained HTML, inline SVG donut + treemap | 400 |
| `src/Cli.fsx` | Argument parsing → `Result<Options, string list>` | 250 |
| `DiskCleanupAdvisor.fsx` | Composition root | 150 |
| `tests/Tests.fsx` | Loads all test modules, runs them | 40 |
| `tests/Test*.fsx` | One test module per source module | ~100 each |

---

## Task Sequence

`Cleaner` (Task 15) is deliberately near-last. Until it exists, the tool is **structurally incapable of modifying anything**, so every earlier task is safe to run against a real home directory.

| # | Task | Deliverable |
|---|---|---|
| 1 | Test harness + Types | `dotnet fsi tests/Tests.fsx` runs and passes |
| 2 | Utilities: formatting + paths | Byte/duration formatting, path normalization |
| 3 | Utilities: glob matching | `**`/`*`/`?` matching with case + Unicode handling |
| 4 | Logging + Diagnostics | Structured stderr logging, throughput counters |
| 5 | Platform + FileSystemOps | OS detection, known folders, real + in-memory FS |
| 6 | Configuration | JSON config load, validate, merge |
| 7 | Rules catalog | macOS rules complete, Linux/Windows seeded |
| 8 | **RiskEngine + adversarial gate tests** | Three gates; the safety core |
| 9 | Report DTO layer | DU→primitive projection (unblocks all JSON) |
| 10 | FileClassifier | Extension/name → Category |
| 11 | DirectoryWalker | Bounded-parallel traversal |
| 12 | Scanner | ScanResult assembly |
| 13 | **Console + JSON + CLI + entry point** | **First end-to-end usable version** |
| 14 | Hashing + Duplicates | Three-stage funnel |
| 15 | **Cleaner + `--delete`** | Trash relocation, journal |
| 16 | Markdown + HTML reports | SVG donut, treemap |
| 17 | Time Machine snapshots + polish | Snapshot reporting, Linux/Windows seeding |

---

### Task 1: Test harness and core types

**Files:**
- Create: `tests/TestFramework.fsx`
- Create: `src/Types.fsx`
- Create: `tests/TestTypes.fsx`
- Create: `tests/Tests.fsx`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `TestFramework.test : string -> (unit -> unit) -> unit`
  - `TestFramework.suite : string -> unit`
  - `TestFramework.assertEqual : 'a -> 'a -> string -> unit` (expected, actual, message)
  - `TestFramework.assertTrue : bool -> string -> unit`
  - `TestFramework.assertContains : string -> string -> string -> unit` (needle, haystack, message)
  - `TestFramework.report : unit -> int` (returns exit code: 0 pass, 1 fail)
  - `Types.Platform`, `Types.RiskLevel`, `Types.Recoverability`, `Types.DeleteStrategy`, `Types.PathSpec`, `Types.Category`, `Types.FsError`, `Types.FileMeta`, `Types.RiskDescription`
  - `Types.RiskLevel.describe : RiskLevel -> RiskDescription`

- [ ] **Step 1: Write the test harness**

Create `tests/TestFramework.fsx`:

```fsharp
(**
    Minimal test harness for DiskCleanupAdvisor.

    No third-party test runner is available in a script-only project, so this
    module provides just enough structure: named tests, a handful of assertions,
    and a process exit code so CI can gate on it.
*)
module TestFramework

open System

/// A single recorded test outcome.
type private Outcome = { Suite: string; Name: string; Error: string option }

let mutable private currentSuite = "(root)"
let private outcomes = ResizeArray<Outcome>()

/// Raised by assertions to signal a test failure without aborting the run.
exception AssertionFailure of string

/// Begins a named group of tests. Subsequent `test` calls are attributed to it.
let suite (name: string) =
    currentSuite <- name
    printfn "\n\027[1m%s\027[0m" name

/// Runs a single named test, capturing any assertion failure or exception.
let test (name: string) (body: unit -> unit) =
    try
        body ()
        outcomes.Add { Suite = currentSuite; Name = name; Error = None }
        printfn "  \027[32m✓\027[0m %s" name
    with
    | AssertionFailure msg ->
        outcomes.Add { Suite = currentSuite; Name = name; Error = Some msg }
        printfn "  \027[31m✗\027[0m %s\n      %s" name msg
    | ex ->
        let msg = sprintf "unexpected %s: %s" (ex.GetType().Name) ex.Message
        outcomes.Add { Suite = currentSuite; Name = name; Error = Some msg }
        printfn "  \027[31m✗\027[0m %s\n      %s" name msg

/// Asserts structural equality, reporting both values on mismatch.
let assertEqual (expected: 'a) (actual: 'a) (message: string) =
    if expected <> actual then
        raise (AssertionFailure(sprintf "%s\n      expected: %A\n      actual:   %A" message expected actual))

/// Asserts a condition holds.
let assertTrue (condition: bool) (message: string) =
    if not condition then raise (AssertionFailure message)

/// Asserts a condition does not hold.
let assertFalse (condition: bool) (message: string) =
    if condition then raise (AssertionFailure message)

/// Asserts that `haystack` contains `needle`.
let assertContains (needle: string) (haystack: string) (message: string) =
    if not (haystack.Contains needle) then
        raise (AssertionFailure(sprintf "%s\n      expected to contain: %s\n      actual: %s" message needle haystack))

/// Asserts a Result is Ok and returns its value.
let assertOk (result: Result<'a, 'e>) (message: string) : 'a =
    match result with
    | Ok v -> v
    | Error e -> raise (AssertionFailure(sprintf "%s\n      expected Ok, got Error: %A" message e))

/// Asserts a Result is Error and returns its error.
let assertError (result: Result<'a, 'e>) (message: string) : 'e =
    match result with
    | Error e -> e
    | Ok v -> raise (AssertionFailure(sprintf "%s\n      expected Error, got Ok: %A" message v))

/// Prints a summary and returns a process exit code (0 = all passed).
let report () : int =
    let failed = outcomes |> Seq.filter (fun o -> o.Error.IsSome) |> Seq.toList
    printfn "\n%s" (String.replicate 60 "─")
    if List.isEmpty failed then
        printfn "\027[32m%d passed\027[0m" outcomes.Count
        0
    else
        printfn "\027[31m%d failed\027[0m, %d passed" failed.Length (outcomes.Count - failed.Length)
        for f in failed do printfn "  \027[31m✗\027[0m %s / %s" f.Suite f.Name
        1
```

- [ ] **Step 2: Verify the harness itself works**

Create a scratch file `tests/_probe.fsx`:

```fsharp
#load "TestFramework.fsx"
open TestFramework
suite "harness self-check"
test "passing test passes" (fun () -> assertEqual 1 1 "one equals one")
test "failing test is recorded" (fun () -> assertEqual 1 2 "deliberate failure")
exit (report ())
```

Run: `dotnet fsi tests/_probe.fsx; echo "exit=$?"`

Expected: one `✓`, one `✗`, summary `1 failed, 1 passed`, and `exit=1`.

Then delete the probe: `rm tests/_probe.fsx`

- [ ] **Step 3: Write the failing test for RiskLevel.describe**

Create `tests/TestTypes.fsx`:

```fsharp
#load "TestFramework.fsx"
#load "../src/Types.fsx"
open TestFramework
open Types

let run () =
    suite "Types.RiskLevel"

    test "every risk level has a non-empty description" (fun () ->
        for level in [ Safe; ProbablySafe; Review; Dangerous; NeverDelete ] do
            let d = RiskLevel.describe level
            assertTrue (d.Description.Length > 0) (sprintf "%A has a description" level)
            assertTrue (d.Icon.Length > 0) (sprintf "%A has an icon" level)
            assertTrue (d.AnsiColor.Length > 0) (sprintf "%A has a color" level))

    test "risk levels order from least to most dangerous" (fun () ->
        assertTrue (RiskLevel.severity Safe < RiskLevel.severity Review) "Safe is less severe than Review"
        assertTrue (RiskLevel.severity Review < RiskLevel.severity NeverDelete) "Review is less severe than NeverDelete")

    test "NeverDelete is the maximum severity" (fun () ->
        let all = [ Safe; ProbablySafe; Review; Dangerous; NeverDelete ]
        let maxLevel = all |> List.maxBy RiskLevel.severity
        assertEqual NeverDelete maxLevel "NeverDelete is most severe")
```

- [ ] **Step 4: Run the test to verify it fails**

Create `tests/Tests.fsx`:

```fsharp
(**
    Test entry point. Loads every test module and runs it.
    Run with:  dotnet fsi tests/Tests.fsx
*)
#load "TestFramework.fsx"
#load "TestTypes.fsx"

TestTypes.run ()

exit (TestFramework.report ())
```

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — `Types.fsx` does not exist, so FSI reports a load error naming `../src/Types.fsx`.

- [ ] **Step 5: Write Types.fsx**

Create `src/Types.fsx`:

```fsharp
(**
    Core domain types for DiskCleanupAdvisor.

    This module contains only type definitions and total, side-effect-free
    functions over them. It has no dependencies, sits at the root of the
    #load graph, and must never acquire one.
*)
module Types

open System

/// The operating systems this tool supports.
type Platform =
    | MacOS
    | Linux
    | Windows

/// How dangerous it is to delete a given item.
/// `NeverDelete` is a veto that no command-line flag can override.
type RiskLevel =
    | Safe
    | ProbablySafe
    | Review
    | Dangerous
    | NeverDelete

/// Presentation metadata for a risk level. Kept out of the DU itself so
/// `RiskLevel` stays comparable and cheap to pattern-match.
type RiskDescription =
    { Icon: string
      AnsiColor: string
      Description: string }

module RiskLevel =
    /// Returns the icon, color, and human-readable meaning of a risk level.
    let describe (level: RiskLevel) : RiskDescription =
        match level with
        | Safe ->
            { Icon = "✓"; AnsiColor = "\027[32m"
              Description = "Regenerable. No user data. Safe to remove." }
        | ProbablySafe ->
            { Icon = "○"; AnsiColor = "\027[36m"
              Description = "Regenerable, but an application may need to re-download it." }
        | Review ->
            { Icon = "?"; AnsiColor = "\027[33m"
              Description = "Requires human judgment. Not deleted automatically." }
        | Dangerous ->
            { Icon = "!"; AnsiColor = "\027[35m"
              Description = "Deletion is likely to break something." }
        | NeverDelete ->
            { Icon = "✕"; AnsiColor = "\027[31m"
              Description = "Protected. This tool will never remove it." }

    /// Orders risk levels from least (0) to most (4) dangerous.
    let severity (level: RiskLevel) : int =
        match level with
        | Safe -> 0
        | ProbablySafe -> 1
        | Review -> 2
        | Dangerous -> 3
        | NeverDelete -> 4

    /// Parses a risk level from configuration text. Case-insensitive.
    let parse (text: string) : Result<RiskLevel, string> =
        match text.Trim().ToLowerInvariant() with
        | "safe" -> Ok Safe
        | "probablysafe" | "probably-safe" -> Ok ProbablySafe
        | "review" -> Ok Review
        | "dangerous" -> Ok Dangerous
        | "neverdelete" | "never-delete" -> Ok NeverDelete
        | other -> Error (sprintf "unknown risk level '%s'" other)

    /// Renders a risk level as stable text for reports and JSON.
    let toText (level: RiskLevel) : string =
        match level with
        | Safe -> "Safe"
        | ProbablySafe -> "ProbablySafe"
        | Review -> "Review"
        | Dangerous -> "Dangerous"
        | NeverDelete -> "NeverDelete"

/// How an item can be recovered after removal.
type Recoverability =
    | Regenerated
    | Redownloadable
    | RecoverableFromTrash
    | Unrecoverable

module Recoverability =
    /// Renders recoverability as stable text for reports and JSON.
    let toText (r: Recoverability) : string =
        match r with
        | Regenerated -> "Regenerated"
        | Redownloadable -> "Redownloadable"
        | RecoverableFromTrash -> "RecoverableFromTrash"
        | Unrecoverable -> "Unrecoverable"

/// How a rule's matched items should be removed.
type DeleteStrategy =
    /// Empty the directory but keep the directory itself (tools may expect it).
    | ContentsOnly
    /// Remove only files matching the rule's include globs.
    | MatchingFiles
    /// Remove the directory itself; it is fully regenerable.
    | WholeDirectory

module DeleteStrategy =
    /// Renders a delete strategy as stable text for reports and JSON.
    let toText (s: DeleteStrategy) : string =
        match s with
        | ContentsOnly -> "ContentsOnly"
        | MatchingFiles -> "MatchingFiles"
        | WholeDirectory -> "WholeDirectory"

/// An unresolved location, resolved against the environment at startup.
type PathSpec =
    | HomeRelative of string
    | Absolute of string
    | EnvRelative of variable: string * relative: string

/// What kind of thing an item is, for grouping in reports.
type Category =
    | Caches
    | Logs
    | TemporaryFiles
    | CrashReports
    | DeveloperArtifacts
    | PackageManagerCache
    | BrowserCache
    | ContainerData
    | VirtualMachines
    | Archives
    | DiskImages
    | Installers
    | LargeFiles
    | DuplicateFiles
    | EmptyDirectories
    | Applications
    | Downloads
    | TrashContents
    | Snapshots
    | Other

module Category =
    /// Renders a category as a human-readable heading.
    let toText (c: Category) : string =
        match c with
        | Caches -> "Caches"
        | Logs -> "Logs"
        | TemporaryFiles -> "Temporary Files"
        | CrashReports -> "Crash Reports"
        | DeveloperArtifacts -> "Developer Artifacts"
        | PackageManagerCache -> "Package Manager Caches"
        | BrowserCache -> "Browser Caches"
        | ContainerData -> "Container Data"
        | VirtualMachines -> "Virtual Machines"
        | Archives -> "Archives"
        | DiskImages -> "Disk Images"
        | Installers -> "Installers"
        | LargeFiles -> "Large Files"
        | DuplicateFiles -> "Duplicate Files"
        | EmptyDirectories -> "Empty Directories"
        | Applications -> "Applications"
        | Downloads -> "Downloads"
        | TrashContents -> "Trash"
        | Snapshots -> "Local Snapshots"
        | Other -> "Other"

    /// Every category, for report iteration and test coverage.
    let all =
        [ Caches; Logs; TemporaryFiles; CrashReports; DeveloperArtifacts
          PackageManagerCache; BrowserCache; ContainerData; VirtualMachines
          Archives; DiskImages; Installers; LargeFiles; DuplicateFiles
          EmptyDirectories; Applications; Downloads; TrashContents; Snapshots; Other ]

/// Why a filesystem operation failed. Collected, never thrown.
type FsError =
    | PermissionDenied of path: string
    | PathNotFound of path: string
    | PathTooLong of path: string
    | IoFailure of path: string * message: string
    | NotSupported of path: string * message: string

module FsError =
    /// The path an error concerns.
    let path (e: FsError) : string =
        match e with
        | PermissionDenied p | PathNotFound p | PathTooLong p -> p
        | IoFailure (p, _) | NotSupported (p, _) -> p

    /// A one-line human-readable rendering.
    let toText (e: FsError) : string =
        match e with
        | PermissionDenied p -> sprintf "permission denied: %s" p
        | PathNotFound p -> sprintf "not found: %s" p
        | PathTooLong p -> sprintf "path too long: %s" p
        | IoFailure (p, m) -> sprintf "I/O error at %s: %s" p m
        | NotSupported (p, m) -> sprintf "unsupported at %s: %s" p m

/// Metadata about a single filesystem entry, captured during traversal.
type FileMeta =
    { Path: string
      SizeBytes: int64
      LastAccess: DateTime
      LastModified: DateTime
      Created: DateTime
      IsDirectory: bool
      /// Non-None when the entry is a symlink or reparse point. Never followed.
      LinkTarget: string option
      Owner: string
      /// Unix mode bits rendered as text (e.g. "rw-r--r--"). Empty on Windows.
      Mode: string }

module FileMeta =
    /// Age since last modification, relative to the supplied clock.
    let age (now: DateTime) (m: FileMeta) : TimeSpan = now - m.LastModified

    /// True when the entry is a link and must not be traversed or deleted.
    let isLink (m: FileMeta) : bool = m.LinkTarget.IsSome
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: 3 tests pass, summary `3 passed`, exit code 0.

Verify the exit code: `dotnet fsi tests/Tests.fsx; echo "exit=$?"` → `exit=0`

- [ ] **Step 7: Commit**

```bash
git add tests/TestFramework.fsx tests/TestTypes.fsx tests/Tests.fsx src/Types.fsx
git commit -m "feat: add test harness and core domain types

Minimal assert/run harness with process exit code, plus the root
type module: Platform, RiskLevel, Recoverability, DeleteStrategy,
PathSpec, Category, FsError, FileMeta."
```

---

### Task 2: Utilities — formatting and path normalization

**Files:**
- Create: `src/Utilities.fsx`
- Create: `tests/TestUtilities.fsx`
- Modify: `tests/Tests.fsx` (add `#load` and `run ()` call)

**Interfaces:**
- Consumes: `Types` (all)
- Produces:
  - `Utilities.Format.bytes : int64 -> string`
  - `Utilities.Format.duration : TimeSpan -> string`
  - `Utilities.Format.rate : float -> string -> string`
  - `Utilities.Path.normalize : string -> string`
  - `Utilities.Path.expandHome : string -> string`
  - `Utilities.Path.isDescendantOf : string -> string -> bool` (ancestor, candidate)
  - `Utilities.Path.segments : string -> string list`
  - `Utilities.Result.traverse : ('a -> Result<'b,'e>) -> 'a list -> Result<'b list, 'e list>`

- [ ] **Step 1: Write the failing tests**

Create `tests/TestUtilities.fsx`:

```fsharp
#load "TestFramework.fsx"
#load "../src/Utilities.fsx"
open System
open TestFramework
open Utilities

let run () =
    suite "Utilities.Format"

    test "bytes renders binary units with one decimal" (fun () ->
        assertEqual "0 B" (Format.bytes 0L) "zero"
        assertEqual "512 B" (Format.bytes 512L) "sub-kilobyte"
        assertEqual "1.0 KB" (Format.bytes 1024L) "exactly one kilobyte"
        assertEqual "1.5 KB" (Format.bytes 1536L) "one and a half kilobytes"
        assertEqual "1.0 MB" (Format.bytes 1048576L) "one megabyte"
        assertEqual "2.5 GB" (Format.bytes 2684354560L) "two and a half gigabytes"
        assertEqual "1.0 TB" (Format.bytes 1099511627776L) "one terabyte")

    test "bytes handles negative values" (fun () ->
        assertEqual "-1.0 KB" (Format.bytes -1024L) "negative kilobyte")

    test "duration renders compactly" (fun () ->
        assertEqual "450ms" (Format.duration (TimeSpan.FromMilliseconds 450.0)) "sub-second"
        assertEqual "1.5s" (Format.duration (TimeSpan.FromSeconds 1.5)) "seconds"
        assertEqual "2m 5s" (Format.duration (TimeSpan.FromSeconds 125.0)) "minutes"
        assertEqual "1h 1m" (Format.duration (TimeSpan.FromMinutes 61.0)) "hours")

    suite "Utilities.Path"

    test "normalize collapses separators and trailing slashes" (fun () ->
        assertEqual "/a/b/c" (Path.normalize "/a/b/c/") "trailing slash removed"
        assertEqual "/a/b/c" (Path.normalize "/a//b///c") "duplicate separators collapsed"
        assertEqual "/" (Path.normalize "/") "root preserved")

    test "normalize resolves dot and dot-dot segments" (fun () ->
        assertEqual "/a/c" (Path.normalize "/a/b/../c") "dot-dot resolved"
        assertEqual "/a/b" (Path.normalize "/a/./b") "dot removed"
        assertEqual "/" (Path.normalize "/a/..") "dot-dot to root")

    test "normalize applies Unicode NFC so macOS NFD paths compare equal" (fun () ->
        // macOS stores filenames decomposed (NFD); user config supplies composed (NFC).
        let composed = "/Users/e/Café"          // é as single codepoint U+00E9
        let decomposed = "/Users/e/Café"  // e + combining acute U+0301
        assertEqual (Path.normalize composed) (Path.normalize decomposed) "NFC and NFD normalize identically")

    test "isDescendantOf requires a proper segment boundary" (fun () ->
        assertTrue (Path.isDescendantOf "/a/b" "/a/b/c") "direct child"
        assertTrue (Path.isDescendantOf "/a/b" "/a/b/c/d") "deeper descendant"
        assertFalse (Path.isDescendantOf "/a/b" "/a/b") "a path is not its own descendant"
        assertFalse (Path.isDescendantOf "/a/b" "/a/bc") "prefix match is not a descendant"
        assertFalse (Path.isDescendantOf "/a/b" "/a") "parent is not a descendant")

    test "isDescendantOf rejects traversal escapes" (fun () ->
        assertFalse (Path.isDescendantOf "/a/b" "/a/b/../../etc/passwd") "dot-dot escape rejected"
        assertTrue (Path.isDescendantOf "/a/b" "/a/b/c/../d") "dot-dot staying inside is allowed")

    test "segments splits a path into its components" (fun () ->
        assertEqual [ "a"; "b"; "c" ] (Path.segments "/a/b/c") "absolute path segments"
        assertEqual ([]: string list) (Path.segments "/") "root has no segments")

    suite "Utilities.Result"

    test "traverse collects all errors rather than stopping at the first" (fun () ->
        let f (n: int) = if n % 2 = 0 then Ok n else Error (sprintf "%d is odd" n)
        let result = Result.traverse f [ 1; 2; 3 ]
        let errors = assertError result "odd inputs produce errors"
        assertEqual [ "1 is odd"; "3 is odd" ] errors "both errors collected")

    test "traverse returns all values when every element succeeds" (fun () ->
        let f (n: int) = Ok (n * 2)
        let values = assertOk (Result.traverse f [ 1; 2; 3 ]) "all succeed"
        assertEqual [ 2; 4; 6 ] values "values in order")
```

- [ ] **Step 2: Wire it into the test entry point**

Modify `tests/Tests.fsx` to read:

```fsharp
(**
    Test entry point. Loads every test module and runs it.
    Run with:  dotnet fsi tests/Tests.fsx
*)
#load "TestFramework.fsx"
#load "TestTypes.fsx"
#load "TestUtilities.fsx"

TestTypes.run ()
TestUtilities.run ()

exit (TestFramework.report ())
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — FSI reports it cannot find `../src/Utilities.fsx`.

- [ ] **Step 4: Write Utilities.fsx**

Create `src/Utilities.fsx`:

```fsharp
(**
    Shared, dependency-light helpers: human-readable formatting, path
    normalization, and Result combinators.

    Path normalization is security-relevant. `isDescendantOf` is the primitive
    the risk engine's allowlist gate is built on, so it must reject prefix
    collisions ("/a/bc" is not inside "/a/b") and traversal escapes.
*)
module Utilities

open System
open System.Globalization
open System.Text

/// Human-readable rendering of sizes, durations, and rates.
module Format =

    let private units = [| "B"; "KB"; "MB"; "GB"; "TB"; "PB" |]

    /// Renders a byte count using binary units, one decimal place above 1 KB.
    let bytes (count: int64) : string =
        let sign = if count < 0L then "-" else ""
        let mutable value = abs (float count)
        let mutable index = 0
        while value >= 1024.0 && index < units.Length - 1 do
            value <- value / 1024.0
            index <- index + 1
        if index = 0 then sprintf "%s%d %s" sign (int value) units.[index]
        else sprintf "%s%.1f %s" sign value units.[index]

    /// Renders a duration compactly, choosing units by magnitude.
    let duration (span: TimeSpan) : string =
        if span.TotalSeconds < 1.0 then sprintf "%dms" (int span.TotalMilliseconds)
        elif span.TotalMinutes < 1.0 then sprintf "%.1fs" span.TotalSeconds
        elif span.TotalHours < 1.0 then sprintf "%dm %ds" span.Minutes span.Seconds
        else sprintf "%dh %dm" (int span.TotalHours) span.Minutes

    /// Renders a per-second rate with a caller-supplied unit label.
    let rate (perSecond: float) (unit: string) : string =
        if Double.IsNaN perSecond || Double.IsInfinity perSecond then sprintf "— %s/s" unit
        elif perSecond >= 1000.0 then sprintf "%s %s/s" (perSecond.ToString("N0", CultureInfo.InvariantCulture)) unit
        else sprintf "%.1f %s/s" perSecond unit

/// Path handling. Every comparison in this module is normalization-first.
module Path =

    let private separator = IO.Path.DirectorySeparatorChar

    /// True when the running platform compares paths case-insensitively.
    /// macOS APFS is case-insensitive by default; Windows always is.
    let platformIsCaseInsensitive =
        Runtime.InteropServices.RuntimeInformation.IsOSPlatform Runtime.InteropServices.OSPlatform.OSX
        || Runtime.InteropServices.RuntimeInformation.IsOSPlatform Runtime.InteropServices.OSPlatform.Windows

    /// Splits a path into its non-empty segments.
    let segments (path: string) : string list =
        path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    /// Normalizes a path for comparison: resolves `.` and `..`, collapses
    /// duplicate separators, removes any trailing separator, and applies
    /// Unicode NFC. The NFC step matters on macOS, which stores filenames
    /// decomposed (NFD) while configuration files supply composed (NFC) text.
    let normalize (path: string) : string =
        if String.IsNullOrWhiteSpace path then ""
        else
            let isAbsolute = path.StartsWith "/" || path.StartsWith "\\"
            let resolved =
                segments path
                |> List.fold (fun acc segment ->
                    match segment with
                    | "." -> acc
                    | ".." -> (match acc with [] -> [] | _ :: rest -> rest)
                    | s -> s :: acc) []
                |> List.rev
            let joined = String.Join(string separator, resolved)
            let withRoot =
                if isAbsolute then string separator + joined
                else joined
            let trimmed =
                if withRoot.Length > 1 && withRoot.EndsWith(string separator)
                then withRoot.TrimEnd separator
                else withRoot
            trimmed.Normalize NormalizationForm.FormC

    /// Expands a leading `~` to the current user's home directory.
    let expandHome (path: string) : string =
        if path = "~" || path.StartsWith "~/" || path.StartsWith "~\\" then
            let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
            IO.Path.Combine(home, path.Substring(min 2 path.Length))
        else path

    /// Compares two normalized paths honoring platform case sensitivity.
    let equals (a: string) (b: string) : bool =
        let comparison =
            if platformIsCaseInsensitive then StringComparison.OrdinalIgnoreCase
            else StringComparison.Ordinal
        String.Equals(normalize a, normalize b, comparison)

    /// True when `candidate` is a strict descendant of `ancestor`.
    ///
    /// Both paths are normalized first, so `..` escapes are resolved before
    /// comparison rather than being string-matched. A path is never its own
    /// descendant, and a shared textual prefix that does not end on a segment
    /// boundary ("/a/bc" against "/a/b") is not a descendant.
    let isDescendantOf (ancestor: string) (candidate: string) : bool =
        let a = normalize ancestor
        let c = normalize candidate
        if a = "" || c = "" then false
        elif equals a c then false
        else
            let comparison =
                if platformIsCaseInsensitive then StringComparison.OrdinalIgnoreCase
                else StringComparison.Ordinal
            let prefix = if a.EndsWith(string separator) then a else a + string separator
            c.StartsWith(prefix, comparison)

/// Combinators over Result that accumulate errors rather than short-circuiting.
module Result =

    /// Applies a fallible function to every element, collecting **all** errors.
    /// Configuration validation reports every problem at once rather than
    /// making the user fix them one run at a time.
    let traverse (f: 'a -> Result<'b, 'e>) (items: 'a list) : Result<'b list, 'e list> =
        let results = items |> List.map f
        let errors = results |> List.choose (function Error e -> Some e | Ok _ -> None)
        if List.isEmpty errors then
            Ok (results |> List.choose (function Ok v -> Some v | Error _ -> None))
        else Error errors

    /// Converts an exception-throwing thunk into a Result.
    let protect (onError: exn -> 'e) (f: unit -> 'a) : Result<'a, 'e> =
        try Ok (f ()) with ex -> Error (onError ex)
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 14 tests total (3 from Task 1, 11 new).

If `normalize` fails the NFC test, confirm the test file itself was written as UTF-8; the two literals must contain genuinely different byte sequences.

- [ ] **Step 6: Commit**

```bash
git add src/Utilities.fsx tests/TestUtilities.fsx tests/Tests.fsx
git commit -m "feat: add formatting, path normalization, and Result combinators

isDescendantOf is the primitive the allowlist gate is built on: it
normalizes both operands first so dot-dot escapes resolve before
comparison, rejects prefix collisions, and applies Unicode NFC so
macOS NFD paths compare equal to config-supplied NFC paths."
```

---

### Task 3: Utilities — glob matching

**Files:**
- Modify: `src/Utilities.fsx` (add `Glob` module at end)
- Modify: `tests/TestUtilities.fsx` (add glob suite)

**Interfaces:**
- Consumes: `Utilities.Path`
- Produces:
  - `Utilities.Glob.matches : string -> string -> bool` (pattern, path)
  - `Utilities.Glob.matchesAny : string list -> string -> bool`

- [ ] **Step 1: Write the failing tests**

Append to the `run ()` function in `tests/TestUtilities.fsx`, before the closing of the function:

```fsharp
    suite "Utilities.Glob"

    test "star matches within a single segment only" (fun () ->
        assertTrue (Glob.matches "*.log" "app.log") "star matches a basename"
        assertTrue (Glob.matches "*.log" "a.log") "star matches short names"
        assertFalse (Glob.matches "*.log" "dir/app.log") "star does not cross a separator"
        assertFalse (Glob.matches "*.log" "app.txt") "extension must match")

    test "question mark matches exactly one character" (fun () ->
        assertTrue (Glob.matches "a?c" "abc") "single character"
        assertFalse (Glob.matches "a?c" "ac") "zero characters does not match"
        assertFalse (Glob.matches "a?c" "abbc") "two characters does not match"
        assertFalse (Glob.matches "a?c" "a/c") "question mark does not match a separator")

    test "double star crosses separators" (fun () ->
        assertTrue (Glob.matches "**/*.log" "a/b/c.log") "deep match"
        assertTrue (Glob.matches "**/*.log" "c.log") "double star matches zero directories"
        assertTrue (Glob.matches "Containers/**" "Containers/a/b/c") "trailing double star"
        assertTrue (Glob.matches "**" "anything/at/all") "bare double star matches everything")

    test "literal patterns match exactly" (fun () ->
        assertTrue (Glob.matches "DerivedData" "DerivedData") "exact literal"
        assertFalse (Glob.matches "DerivedData" "DerivedDataX") "no implicit prefix match")

    test "matching is case-insensitive on macOS and Windows" (fun () ->
        // APFS is case-insensitive by default, so a rule written as "Caches"
        // must still match a directory reported as "caches".
        if Path.platformIsCaseInsensitive then
            assertTrue (Glob.matches "*.LOG" "app.log") "case-insensitive on this platform"
        else
            assertFalse (Glob.matches "*.LOG" "app.log") "case-sensitive on this platform")

    test "regex metacharacters in patterns are treated literally" (fun () ->
        assertTrue (Glob.matches "a.b" "a.b") "dot is literal"
        assertFalse (Glob.matches "a.b" "axb") "dot does not match any character"
        assertTrue (Glob.matches "v1.0+build" "v1.0+build") "plus is literal")

    test "matchesAny succeeds when any pattern matches" (fun () ->
        assertTrue (Glob.matchesAny [ "*.tmp"; "*.log" ] "app.log") "second pattern matches"
        assertFalse (Glob.matchesAny [ "*.tmp"; "*.log" ] "app.txt") "no pattern matches"
        assertFalse (Glob.matchesAny [] "app.log") "empty pattern list matches nothing")
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — `The value, namespace, type or module 'Glob' is not defined.`

- [ ] **Step 3: Implement the Glob module**

Append to `src/Utilities.fsx`:

```fsharp
/// Glob matching for rule include/exclude patterns.
///
/// Patterns are translated to anchored regular expressions and cached. The
/// translation escapes every regex metacharacter, so a pattern like `v1.0+build`
/// matches literally rather than being interpreted as a regex.
module Glob =

    open System.Collections.Concurrent
    open System.Text.RegularExpressions

    let private cache = ConcurrentDictionary<string * bool, Regex>()

    /// Translates a glob pattern into an anchored regex source string.
    ///
    /// `**` crosses separators (and matches zero of them, so `**/x` matches a
    /// bare `x`); `*` and `?` do not cross separators.
    let private translate (pattern: string) : string =
        let sb = StringBuilder()
        sb.Append '^' |> ignore
        let mutable i = 0
        while i < pattern.Length do
            match pattern.[i] with
            | '*' when i + 1 < pattern.Length && pattern.[i + 1] = '*' ->
                // `**/` should also match zero directories, so consume an
                // optional following separator as part of the same group.
                if i + 2 < pattern.Length && (pattern.[i + 2] = '/' || pattern.[i + 2] = '\\') then
                    sb.Append "(?:.*[/\\\\])?" |> ignore
                    i <- i + 3
                else
                    sb.Append ".*" |> ignore
                    i <- i + 2
            | '*' ->
                sb.Append "[^/\\\\]*" |> ignore
                i <- i + 1
            | '?' ->
                sb.Append "[^/\\\\]" |> ignore
                i <- i + 1
            | '/' | '\\' ->
                sb.Append "[/\\\\]" |> ignore
                i <- i + 1
            | c ->
                sb.Append(Regex.Escape(string c)) |> ignore
                i <- i + 1
        sb.Append '$' |> ignore
        sb.ToString()

    /// Compiles and caches the regex for a pattern.
    let private regexFor (pattern: string) : Regex =
        let key = (pattern, Path.platformIsCaseInsensitive)
        cache.GetOrAdd(key, fun (p, insensitive) ->
            let options =
                if insensitive then RegexOptions.Compiled ||| RegexOptions.IgnoreCase
                else RegexOptions.Compiled
            Regex(translate p, options))

    /// True when `path` matches `pattern`. Case sensitivity follows the platform.
    let matches (pattern: string) (path: string) : bool =
        (regexFor pattern).IsMatch path

    /// True when `path` matches any of the supplied patterns.
    /// An empty pattern list matches nothing.
    let matchesAny (patterns: string list) (path: string) : bool =
        patterns |> List.exists (fun p -> matches p path)
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 21 tests total.

- [ ] **Step 5: Commit**

```bash
git add src/Utilities.fsx tests/TestUtilities.fsx
git commit -m "feat: add cached glob matching for rule patterns

Globs translate to anchored regexes with every metacharacter escaped,
so patterns match literally. ** crosses separators and matches zero
directories; * and ? stop at a separator."
```

---

### Task 4: Logging and Diagnostics

**Files:**
- Create: `src/Logging.fsx`
- Create: `src/Diagnostics.fsx`
- Create: `tests/TestDiagnostics.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`
- Produces:
  - `Logging.Level` = `Debug | Info | Warn | Error`
  - `Logging.create : Level -> Logger`
  - `Logger` members: `.Debug : string -> unit`, `.Info`, `.Warn`, `.Error`, `.IsEnabled : Level -> bool`
  - `Diagnostics.Counters` (record with mutable fields, documented as a deliberate performance exception)
  - `Diagnostics.create : unit -> Counters`
  - `Diagnostics.snapshot : Counters -> Snapshot`
  - `Diagnostics.render : Snapshot -> string`

- [ ] **Step 1: Write the failing tests**

Create `tests/TestDiagnostics.fsx`:

```fsharp
#load "TestFramework.fsx"
#load "../src/Diagnostics.fsx"
open System
open TestFramework
open Diagnostics

let run () =
    suite "Diagnostics"

    test "counters start at zero" (fun () ->
        let c = create ()
        let s = snapshot c
        assertEqual 0L s.Directories "directories"
        assertEqual 0L s.Files "files"
        assertEqual 0L s.Bytes "bytes")

    test "counters accumulate" (fun () ->
        let c = create ()
        recordDirectory c
        recordFile c 1024L
        recordFile c 2048L
        let s = snapshot c
        assertEqual 1L s.Directories "one directory"
        assertEqual 2L s.Files "two files"
        assertEqual 3072L s.Bytes "summed bytes")

    test "skips are counted by reason" (fun () ->
        let c = create ()
        recordSkip c SkipReason.Permission
        recordSkip c SkipReason.Permission
        recordSkip c SkipReason.Symlink
        let s = snapshot c
        assertEqual 2L (s.SkipsByReason |> Map.find SkipReason.Permission) "permission skips"
        assertEqual 1L (s.SkipsByReason |> Map.find SkipReason.Symlink) "symlink skips")

    test "rates are computed against elapsed time" (fun () ->
        let c = create ()
        for _ in 1 .. 100 do recordFile c 1000L
        Threading.Thread.Sleep 50
        let s = snapshot c
        assertTrue (s.FilesPerSecond > 0.0) "positive file rate"
        assertTrue (s.BytesPerSecond > 0.0) "positive byte rate")

    test "render includes every reported metric" (fun () ->
        let c = create ()
        recordDirectory c
        recordFile c 4096L
        let text = render (snapshot c)
        assertContains "Elapsed" text "elapsed present"
        assertContains "Directories" text "directories present"
        assertContains "Files" text "files present"
        assertContains "Memory" text "memory present")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add `#load "TestDiagnostics.fsx"` after the existing loads, and `TestDiagnostics.run ()` after the existing run calls.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/Diagnostics.fsx`.

- [ ] **Step 4: Write Logging.fsx**

Create `src/Logging.fsx`:

```fsharp
(**
    Leveled structured logging.

    All log output goes to **stderr** so that stdout carries only the report.
    This keeps `dotnet fsi DiskCleanupAdvisor.fsx --json /dev/stdout` usable in
    a pipeline without log lines corrupting the JSON.
*)
module Logging

open System

/// Log severity, ordered from most to least verbose.
type Level =
    | Debug
    | Info
    | Warn
    | Error

module Level =
    /// Numeric rank used for threshold comparison.
    let rank (level: Level) : int =
        match level with
        | Debug -> 0
        | Info -> 1
        | Warn -> 2
        | Error -> 3

    /// Fixed-width label for aligned output.
    let label (level: Level) : string =
        match level with
        | Debug -> "DEBUG"
        | Info -> "INFO "
        | Warn -> "WARN "
        | Error -> "ERROR"

    /// ANSI color for the level label.
    let color (level: Level) : string =
        match level with
        | Debug -> "\027[90m"
        | Info -> "\027[36m"
        | Warn -> "\027[33m"
        | Error -> "\027[31m"

/// A logger bound to a minimum severity threshold.
type Logger =
    { Threshold: Level
      UseColor: bool }

/// True when color output is appropriate: stderr is not redirected and
/// the NO_COLOR convention has not been set.
let colorSupported () : bool =
    not (Console.IsErrorRedirected)
    && String.IsNullOrEmpty(Environment.GetEnvironmentVariable "NO_COLOR")

/// Creates a logger that emits records at or above the given level.
let create (threshold: Level) : Logger =
    { Threshold = threshold; UseColor = colorSupported () }

/// True when the logger would emit a record at this level.
let isEnabled (level: Level) (logger: Logger) : bool =
    Level.rank level >= Level.rank logger.Threshold

/// Writes one record to stderr if the level meets the threshold.
let log (level: Level) (message: string) (logger: Logger) : unit =
    if isEnabled level logger then
        let stamp = DateTime.Now.ToString "HH:mm:ss.fff"
        if logger.UseColor then
            eprintfn "\027[90m%s\027[0m %s%s\027[0m %s" stamp (Level.color level) (Level.label level) message
        else
            eprintfn "%s %s %s" stamp (Level.label level) message

/// Writes a debug-level record.
let debug (message: string) (logger: Logger) : unit = log Debug message logger

/// Writes an info-level record.
let info (message: string) (logger: Logger) : unit = log Info message logger

/// Writes a warning-level record.
let warn (message: string) (logger: Logger) : unit = log Warn message logger

/// Writes an error-level record.
let error (message: string) (logger: Logger) : unit = log Error message logger
```

- [ ] **Step 5: Write Diagnostics.fsx**

Create `src/Diagnostics.fsx`:

```fsharp
(**
    Traversal counters and throughput reporting.

    This module is the one deliberate exception to the project's immutability
    rule. Counters are incremented once per filesystem entry from multiple
    threads; allocating a new record per increment would dominate the cost of
    the scan itself. Updates use Interlocked so the totals stay correct under
    parallel traversal.
*)
module Diagnostics

open System
open System.Diagnostics
open System.Threading

#load "Utilities.fsx"
open Utilities

/// Why a directory or file was not examined.
type SkipReason =
    | Permission
    | Symlink
    | FilesystemBoundary
    | DepthLimit
    | Excluded

module SkipReason =
    /// Human-readable label for reports.
    let toText (r: SkipReason) : string =
        match r with
        | Permission -> "permission denied"
        | Symlink -> "symlink"
        | FilesystemBoundary -> "different filesystem"
        | DepthLimit -> "depth limit"
        | Excluded -> "excluded by config"

    /// Every reason, so reports can show zero counts explicitly.
    let all = [ Permission; Symlink; FilesystemBoundary; DepthLimit; Excluded ]

/// Live, thread-safe traversal counters.
type Counters =
    { Clock: Stopwatch
      mutable Directories: int64
      mutable Files: int64
      mutable Bytes: int64
      Skips: Collections.Concurrent.ConcurrentDictionary<SkipReason, int64 ref> }

/// An immutable point-in-time reading of the counters.
type Snapshot =
    { Elapsed: TimeSpan
      Directories: int64
      Files: int64
      Bytes: int64
      SkipsByReason: Map<SkipReason, int64>
      PeakMemoryBytes: int64
      DirectoriesPerSecond: float
      FilesPerSecond: float
      BytesPerSecond: float }

/// Creates a fresh counter set with its clock already running.
let create () : Counters =
    { Clock = Stopwatch.StartNew()
      Directories = 0L
      Files = 0L
      Bytes = 0L
      Skips = Collections.Concurrent.ConcurrentDictionary() }

/// Records that one directory was examined.
let recordDirectory (c: Counters) : unit =
    Interlocked.Increment(&c.Directories) |> ignore

/// Records that one file of the given size was examined.
let recordFile (c: Counters) (sizeBytes: int64) : unit =
    Interlocked.Increment(&c.Files) |> ignore
    Interlocked.Add(&c.Bytes, sizeBytes) |> ignore

/// Records that one entry was skipped for the given reason.
let recordSkip (c: Counters) (reason: SkipReason) : unit =
    let cell = c.Skips.GetOrAdd(reason, fun _ -> ref 0L)
    Interlocked.Increment cell |> ignore

/// Takes an immutable reading of the current counters.
let snapshot (c: Counters) : Snapshot =
    let elapsed = c.Clock.Elapsed
    let seconds = max elapsed.TotalSeconds 0.001
    let directories = Interlocked.Read &c.Directories
    let files = Interlocked.Read &c.Files
    let bytes = Interlocked.Read &c.Bytes
    let skips =
        SkipReason.all
        |> List.map (fun r ->
            match c.Skips.TryGetValue r with
            | true, cell -> r, Interlocked.Read cell
            | _ -> r, 0L)
        |> Map.ofList
    { Elapsed = elapsed
      Directories = directories
      Files = files
      Bytes = bytes
      SkipsByReason = skips
      PeakMemoryBytes = GC.GetTotalMemory false
      DirectoriesPerSecond = float directories / seconds
      FilesPerSecond = float files / seconds
      BytesPerSecond = float bytes / seconds }

/// Renders a snapshot as an aligned multi-line block for the console report.
let render (s: Snapshot) : string =
    let lines =
        [ sprintf "  Elapsed         %s" (Format.duration s.Elapsed)
          sprintf "  Directories     %d  (%s)" s.Directories (Format.rate s.DirectoriesPerSecond "dir")
          sprintf "  Files           %d  (%s)" s.Files (Format.rate s.FilesPerSecond "file")
          sprintf "  Examined        %s  (%s)" (Format.bytes s.Bytes) (Format.rate s.BytesPerSecond "B")
          sprintf "  Memory          %s" (Format.bytes s.PeakMemoryBytes) ]
    let skipLines =
        s.SkipsByReason
        |> Map.toList
        |> List.filter (fun (_, count) -> count > 0L)
        |> List.map (fun (reason, count) -> sprintf "  Skipped         %d (%s)" count (SkipReason.toText reason))
    String.Join("\n", lines @ skipLines)
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 26 tests total.

- [ ] **Step 7: Verify logging renders correctly by hand**

Run:

```bash
cat > /tmp/logprobe.fsx <<'EOF'
#load "src/Logging.fsx"
let log = Logging.create Logging.Debug
log |> Logging.debug "a debug line"
log |> Logging.info "an info line"
log |> Logging.warn "a warning"
log |> Logging.error "an error"
printfn "stdout is clean"
EOF
dotnet fsi /tmp/logprobe.fsx 2>/dev/null
```

Expected: **only** `stdout is clean` appears. All four log lines go to stderr and are suppressed by `2>/dev/null`. This confirms the stdout/stderr split that makes `--json /dev/stdout` safe.

Then: `dotnet fsi /tmp/logprobe.fsx 2>&1 | grep -c "line\|warning\|error"` → `4`

Clean up: `rm /tmp/logprobe.fsx`

- [ ] **Step 8: Commit**

```bash
git add src/Logging.fsx src/Diagnostics.fsx tests/TestDiagnostics.fsx tests/Tests.fsx
git commit -m "feat: add leveled logging and traversal diagnostics

Logs go to stderr so stdout carries only report output. Counters use
Interlocked rather than immutable updates — the one deliberate
mutability exception, documented in the module header, because they
are incremented once per filesystem entry across threads."
```

---

**Remaining tasks (5–17) follow in the next section of this plan.**

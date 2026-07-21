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
- **No source or test module may contain a `#load` directive.** See the load-order rule below. This is the single most important structural constraint in the project.

### The `#load` rule (verified empirically — read before writing any file)

`#load` is **not** idempotent. A file reached through two separate `#load` directives is **evaluated twice**, running its top-level side effects twice. This was confirmed by direct experiment, not assumed.

The consequence:

> **Exactly two files in the entire project contain `#load` directives:
> `DiskCleanupAdvisor.fsx` and `tests/Tests.fsx`.**
> Each lists every file it needs exactly once, in topological order.
> **No `src/*.fsx` and no `tests/Test*.fsx` may contain a `#load` line.**

Modules still reference each other freely (`Scanner.make`, `Types.Safe`) — F# resolves those from the load order established by the owning file. A module does not need to `#load` a dependency in order to `open` or reference it; it only needs that dependency to appear earlier in the list.

Verified behavior, for reference:

| Shape | Result |
|---|---|
| Entry lists all files once, modules have no internal loads | ✅ Each file evaluated exactly once |
| Entry lists a file that a dependency also loads | ❌ That file evaluated twice |
| Two test files each loading a shared harness | ❌ Harness evaluated twice, and nested `module X =` inside a loaded file fails to resolve |

A test file needs no `module` declaration — FSI names it implicitly after the filename, so `tests/TestTypes.fsx` is reachable as `TestTypes`. It must expose `run : unit -> unit`. Do **not** use an indented `module TestX =` block; that nests the name and breaks resolution.

Each task's "Interfaces → Consumes" line tells you the ordering requirement; the actual `#load` goes in `tests/Tests.fsx` (and, from Task 13, `DiskCleanupAdvisor.fsx`).

### Canonical load order

This is the topological order. Every owner file and every throwaway probe script lists a **prefix** of this list — all entries up to and including the module it needs, skipping none.

```
src/Types.fsx
src/Utilities.fsx
src/Logging.fsx
src/Diagnostics.fsx
src/Platform.fsx
src/Configuration.fsx
src/Rules.fsx
src/RiskEngine.fsx
src/FileClassifier.fsx
src/DirectoryWalker.fsx
src/Scanner.fsx
src/Hashing.fsx          (Task 14)
src/Duplicates.fsx       (Task 14)
src/Cleaner.fsx          (Task 15)
src/Report/Dto.fsx
src/Report/Json.fsx
src/Report/Console.fsx
src/Report/Markdown.fsx  (Task 16)
src/Report/Html.fsx      (Task 16)
src/Cli.fsx
```

`tests/Tests.fsx` lists this whole prefix (paths prefixed `../`), then `TestFramework.fsx`, then each `TestX.fsx` in any order.

**Probe scripts in the verification steps below show only the module under test.** Replace that single line with the full prefix from this list — a probe loading just `src/RiskEngine.fsx` will fail, because `Types` and `Utilities` would not be loaded. For example, the Task 8 veto probe begins:

```fsharp
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/RiskEngine.fsx"
```

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
    Test entry point, and one of only two files in the project permitted to
    contain #load directives. Lists every file exactly once in topological
    order — see the Canonical load order in the plan header.

    Run with:  dotnet fsi tests/Tests.fsx
*)
#load "../src/Types.fsx"
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
    Test entry point. Every file listed exactly once, in topological order.
    Run with:  dotnet fsi tests/Tests.fsx
*)
#load "../src/Types.fsx"
#load "../src/Utilities.fsx"
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
#load "src/Types.fsx"
#load "src/Utilities.fsx"
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

### Task 5: Platform detection and FileSystemOps

**Files:**
- Create: `src/Platform.fsx`
- Create: `tests/TestPlatform.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`
- Produces:
  - `Platform.current : unit -> Types.Platform`
  - `Platform.home : unit -> string`
  - `Platform.isElevated : unit -> bool`
  - `Platform.resolve : PathSpec -> string option` (None when the variable is unset)
  - `Platform.FileSystemOps` (record type — see below)
  - `Platform.FileSystem.real : FileSystemOps`
  - `Platform.FileSystem.inMemory : TreeEntry list -> FileSystemOps`
  - `Platform.TreeEntry` = `{ Path: string; SizeBytes: int64; IsDirectory: bool; LinkTarget: string option; ModifiedDaysAgo: float }`

**Verified environment facts for this task:**
- `Environment.SpecialFolder.UserProfile` → `/Users/etholpalmer` on macOS.
- `Environment.SpecialFolder.ApplicationData` and `LocalApplicationData` both → `~/Library/Application Support` on macOS. They are **not** distinct there; do not rely on the difference.
- `DriveInfo("/").DriveFormat` → `apfs`. Use `DriveInfo.Name` for filesystem-boundary comparison.
- `EnumerationOptions(IgnoreInaccessible = true)` suppresses permission exceptions during enumeration. The walker still needs explicit `Result` handling for the *initial* directory open, which `IgnoreInaccessible` does not cover.

- [ ] **Step 1: Write the failing tests**

Create `tests/TestPlatform.fsx`:

```fsharp
open System
open TestFramework
open Types
open Platform

let run () =
    suite "Platform"

    test "current returns one of the three supported platforms" (fun () ->
        let p = current ()
        assertTrue (p = MacOS || p = Linux || p = Windows) "recognized platform")

    test "home resolves to an existing directory" (fun () ->
        let h = home ()
        assertTrue (h.Length > 0) "home is non-empty"
        assertTrue (IO.Directory.Exists h) "home exists")

    test "resolve expands a home-relative spec" (fun () ->
        match resolve (HomeRelative "Library/Caches") with
        | Some path ->
            assertTrue (path.StartsWith(home ())) "resolved under home"
            assertContains "Caches" path "retains the relative part"
        | None -> assertTrue false "HomeRelative should always resolve")

    test "resolve returns None for an unset environment variable" (fun () ->
        assertEqual None (resolve (EnvRelative("DEFINITELY_NOT_SET_XYZ", "sub"))) "unset variable yields None")

    test "resolve passes an absolute spec through unchanged" (fun () ->
        assertEqual (Some "/tmp") (resolve (Absolute "/tmp")) "absolute preserved")

    suite "Platform.FileSystem.inMemory"

    let tree =
        [ { Path = "/root"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
          { Path = "/root/a.log"; SizeBytes = 100L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 10.0 }
          { Path = "/root/b.txt"; SizeBytes = 200L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 2.0 }
          { Path = "/root/sub"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
          { Path = "/root/sub/c.log"; SizeBytes = 300L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 90.0 }
          { Path = "/root/link"; SizeBytes = 0L; IsDirectory = false; LinkTarget = Some "/elsewhere"; ModifiedDaysAgo = 1.0 } ]

    let fs = FileSystem.inMemory tree

    test "enumerates immediate children only" (fun () ->
        let files = assertOk (fs.EnumerateFiles "/root") "enumerate /root files"
        assertEqual 3 files.Length "a.log, b.txt, link — not sub/c.log"
        let dirs = assertOk (fs.EnumerateDirectories "/root") "enumerate /root dirs"
        assertEqual [| "/root/sub" |] dirs "one subdirectory")

    test "reports metadata for a known file" (fun () ->
        let meta = assertOk (fs.GetInfo "/root/a.log") "get info"
        assertEqual 100L meta.SizeBytes "size"
        assertFalse meta.IsDirectory "not a directory"
        assertEqual None meta.LinkTarget "not a link")

    test "reports link targets so the walker can refuse to follow them" (fun () ->
        let meta = assertOk (fs.GetInfo "/root/link") "get link info"
        assertTrue (FileMeta.isLink meta) "recognized as a link")

    test "returns PathNotFound for an absent path" (fun () ->
        let err = assertError (fs.GetInfo "/root/nope") "absent path"
        assertEqual (PathNotFound "/root/nope") err "PathNotFound")

    test "modified times are back-dated so age rules can be tested" (fun () ->
        let meta = assertOk (fs.GetInfo "/root/sub/c.log") "get aged file"
        let age = FileMeta.age DateTime.UtcNow meta
        assertTrue (age.TotalDays > 89.0 && age.TotalDays < 91.0) "roughly 90 days old")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestPlatform.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestPlatform.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/Platform.fsx`.

- [ ] **Step 4: Write Platform.fsx**

Create `src/Platform.fsx`:

```fsharp
(**
    Platform detection, known-folder resolution, and the filesystem boundary.

    All filesystem access in this project goes through `FileSystemOps`, a record
    of functions rather than an interface. Two implementations exist: `real`,
    which touches the disk, and `inMemory`, which serves a synthetic tree. Every
    module above this one is written against the record, so the entire scanning
    and risk pipeline is testable without creating a single file.

    Every operation returns Result. Permission denial is the common case on
    macOS, not an edge case: TCC blocks ~/Library/Containers, ~/Library/Mail,
    and others unless the invoking terminal has Full Disk Access.
*)
module Platform

open System
open System.IO
open System.Runtime.InteropServices

open Types
open Utilities

/// The operating system this process is running on.
let current () : Types.Platform =
    if RuntimeInformation.IsOSPlatform OSPlatform.OSX then MacOS
    elif RuntimeInformation.IsOSPlatform OSPlatform.Windows then Windows
    else Linux

/// The invoking user's home directory.
let home () : string =
    Environment.GetFolderPath Environment.SpecialFolder.UserProfile

/// True when the process is running with elevated privileges.
///
/// The tool refuses to run elevated unless `--allow-root` is passed, because
/// an elevated scan can reach — and a bug could then remove — system files
/// that an ordinary user account is protected from.
let isElevated () : bool =
    match current () with
    | Windows ->
        // On Windows, an elevated process can write to the system directory.
        try
            let probe = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.Windows, "elevation.probe")
            use _ = File.Create(probe, 1, FileOptions.DeleteOnClose)
            true
        with _ -> false
    | MacOS | Linux ->
        // getuid() returns 0 for root. SUDO_USER additionally catches `sudo`.
        Environment.GetEnvironmentVariable "USER" = "root"
        || not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "SUDO_USER"))

/// Resolves a PathSpec against the current environment.
/// Returns None when an environment variable the spec depends on is unset,
/// which is how rules for absent tooling are dropped at startup.
let resolve (spec: PathSpec) : string option =
    match spec with
    | Absolute path -> Some (Utilities.Path.normalize path)
    | HomeRelative relative ->
        Some (Utilities.Path.normalize (Path.Combine(home (), relative)))
    | EnvRelative (variable, relative) ->
        match Environment.GetEnvironmentVariable variable with
        | null | "" -> None
        | value -> Some (Utilities.Path.normalize (Path.Combine(value, relative)))

/// The filesystem operations every other module depends on.
/// A record of functions rather than an interface, so substituting a fake in
/// tests is a value assignment rather than a type hierarchy.
type FileSystemOps =
    { /// Immediate subdirectories of a directory.
      EnumerateDirectories: string -> Result<string[], FsError>
      /// Immediate files within a directory.
      EnumerateFiles: string -> Result<string[], FsError>
      /// Metadata for a single entry.
      GetInfo: string -> Result<FileMeta, FsError>
      /// Opens a file for reading. The caller disposes the stream.
      OpenRead: string -> Result<Stream, FsError>
      /// True when the path exists as a file or directory.
      Exists: string -> bool
      /// The filesystem identity of a path, for boundary detection.
      VolumeOf: string -> Result<string, FsError> }

/// A synthetic filesystem entry for the in-memory implementation.
type TreeEntry =
    { Path: string
      SizeBytes: int64
      IsDirectory: bool
      LinkTarget: string option
      /// How long ago the entry was last modified, so age rules are testable.
      ModifiedDaysAgo: float }

module FileSystem =

    /// Converts a BCL exception into the corresponding FsError.
    let private classify (path: string) (ex: exn) : FsError =
        match ex with
        | :? UnauthorizedAccessException -> PermissionDenied path
        | :? DirectoryNotFoundException | :? FileNotFoundException -> PathNotFound path
        | :? PathTooLongException -> PathTooLong path
        | :? NotSupportedException as e -> NotSupported(path, e.Message)
        | e -> IoFailure(path, e.Message)

    /// Enumeration settings shared by the real implementation.
    /// IgnoreInaccessible lets a partially-readable directory still yield its
    /// readable entries instead of failing wholesale.
    let private enumerationOptions =
        EnumerationOptions(
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false)

    /// The real filesystem.
    let real: FileSystemOps =
        { EnumerateDirectories = fun path ->
            try Ok (Directory.GetDirectories(path, "*", enumerationOptions))
            with ex -> Error (classify path ex)

          EnumerateFiles = fun path ->
            try Ok (Directory.GetFiles(path, "*", enumerationOptions))
            with ex -> Error (classify path ex)

          GetInfo = fun path ->
            try
                let isDirectory = Directory.Exists path
                if not isDirectory && not (File.Exists path) then Error (PathNotFound path)
                else
                    let info: FileSystemInfo =
                        if isDirectory then DirectoryInfo path :> FileSystemInfo
                        else FileInfo path :> FileSystemInfo
                    let size =
                        match info with
                        | :? FileInfo as f -> f.Length
                        | _ -> 0L
                    let mode =
                        if current () = Windows then ""
                        else
                            try string (File.GetUnixFileMode path) with _ -> ""
                    Ok { Path = path
                         SizeBytes = size
                         LastAccess = info.LastAccessTimeUtc
                         LastModified = info.LastWriteTimeUtc
                         Created = info.CreationTimeUtc
                         IsDirectory = isDirectory
                         LinkTarget = (if isNull info.LinkTarget then None else Some info.LinkTarget)
                         Owner = Environment.UserName
                         Mode = mode }
            with ex -> Error (classify path ex)

          OpenRead = fun path ->
            try Ok (File.OpenRead path :> Stream)
            with ex -> Error (classify path ex)

          Exists = fun path -> File.Exists path || Directory.Exists path

          VolumeOf = fun path ->
            try
                let root = Path.GetPathRoot(Path.GetFullPath path)
                Ok (DriveInfo(root).Name)
            with ex -> Error (classify path ex) }

    /// A synthetic filesystem backed by a list of entries.
    /// Paths are matched after normalization, so tests may write them in any
    /// separator style.
    let inMemory (entries: TreeEntry list) : FileSystemOps =
        let now = DateTime.UtcNow
        let byPath =
            entries
            |> List.map (fun e -> Utilities.Path.normalize e.Path, e)
            |> Map.ofList

        let toMeta (e: TreeEntry) : FileMeta =
            let modified = now.AddDays(-e.ModifiedDaysAgo)
            { Path = e.Path
              SizeBytes = e.SizeBytes
              LastAccess = modified
              LastModified = modified
              Created = modified
              IsDirectory = e.IsDirectory
              LinkTarget = e.LinkTarget
              Owner = "testuser"
              Mode = "rw-r--r--" }

        /// Immediate children of a directory: descendants with exactly one
        /// additional path segment.
        let childrenOf (parent: string) =
            let p = Utilities.Path.normalize parent
            let depth = (Utilities.Path.segments p).Length
            byPath
            |> Map.toList
            |> List.map snd
            |> List.filter (fun e ->
                Utilities.Path.isDescendantOf p e.Path
                && (Utilities.Path.segments (Utilities.Path.normalize e.Path)).Length = depth + 1)

        { EnumerateDirectories = fun path ->
            if not (byPath.ContainsKey(Utilities.Path.normalize path)) then Error (PathNotFound path)
            else Ok (childrenOf path |> List.filter (fun e -> e.IsDirectory)
                                     |> List.map (fun e -> e.Path) |> List.toArray)

          EnumerateFiles = fun path ->
            if not (byPath.ContainsKey(Utilities.Path.normalize path)) then Error (PathNotFound path)
            else Ok (childrenOf path |> List.filter (fun e -> not e.IsDirectory)
                                     |> List.map (fun e -> e.Path) |> List.toArray)

          GetInfo = fun path ->
            match byPath.TryFind(Utilities.Path.normalize path) with
            | Some e -> Ok (toMeta e)
            | None -> Error (PathNotFound path)

          OpenRead = fun path ->
            match byPath.TryFind(Utilities.Path.normalize path) with
            | Some e when not e.IsDirectory ->
                // Deterministic synthetic content sized to match the entry, so
                // hashing tests produce stable results.
                Ok (new MemoryStream(Array.create (int e.SizeBytes) 0uy) :> Stream)
            | Some _ -> Error (NotSupported(path, "cannot read a directory"))
            | None -> Error (PathNotFound path)

          Exists = fun path -> byPath.ContainsKey(Utilities.Path.normalize path)

          VolumeOf = fun _ -> Ok "/testvolume" }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 36 tests total.

- [ ] **Step 6: Commit**

```bash
git add src/Platform.fsx tests/TestPlatform.fsx tests/Tests.fsx
git commit -m "feat: add platform detection and FileSystemOps abstraction

FileSystemOps is a record of functions with real and in-memory
implementations, so the scanning and risk pipeline is testable
without touching disk. Real enumeration uses IgnoreInaccessible so a
partially-readable directory still yields its readable entries."
```

---

### Task 6: Configuration

**Files:**
- Create: `src/Configuration.fsx`
- Create: `tests/TestConfiguration.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`, `Platform`
- Produces:
  - `Configuration.Config` = `{ IgnorePaths: string list; IgnoreExtensions: string list; MinSizeBytes: int64; AgeThresholdDays: int; ThreadCount: int; HashAlgorithm: string; RiskOverrides: Map<string, RiskLevel> }`
  - `Configuration.defaults : Config`
  - `Configuration.parse : string -> Result<Config, string list>`
  - `Configuration.load : FileSystemOps -> string option -> Result<Config, string list>`
  - `Configuration.defaultPath : unit -> string`

**Critical rule:** a `riskOverride` may only **raise** severity. An override lowering a rule's risk is a validation **error**, not a silent clamp — silently ignoring it would leave the user believing they had loosened a restriction.

- [ ] **Step 1: Write the failing tests**

Create `tests/TestConfiguration.fsx`:

```fsharp
open TestFramework
open Types
open Configuration

let run () =
    suite "Configuration.parse"

    test "an empty object yields the defaults" (fun () ->
        let c = assertOk (parse "{}") "empty object parses"
        assertEqual defaults.MinSizeBytes c.MinSizeBytes "default min size"
        assertEqual defaults.ThreadCount c.ThreadCount "default thread count")

    test "supplied values override the defaults" (fun () ->
        let json = """{"minSizeBytes": 2048, "ageThresholdDays": 60, "threadCount": 4}"""
        let c = assertOk (parse json) "parses"
        assertEqual 2048L c.MinSizeBytes "min size"
        assertEqual 60 c.AgeThresholdDays "age threshold"
        assertEqual 4 c.ThreadCount "thread count")

    test "ignore paths are expanded and normalized" (fun () ->
        let json = """{"ignorePaths": ["~/Projects/active", "/tmp//x/"]}"""
        let c = assertOk (parse json) "parses"
        assertEqual 2 c.IgnorePaths.Length "two paths"
        assertFalse (c.IgnorePaths |> List.exists (fun p -> p.StartsWith "~")) "tilde expanded"
        assertTrue (c.IgnorePaths |> List.contains "/tmp/x") "normalized")

    test "extensions are normalized to lowercase with a leading dot" (fun () ->
        let json = """{"ignoreExtensions": ["PSD", ".Sketch"]}"""
        let c = assertOk (parse json) "parses"
        assertEqual [ ".psd"; ".sketch" ] c.IgnoreExtensions "normalized extensions")

    test "malformed JSON reports an error rather than throwing" (fun () ->
        let errors = assertError (parse "{ not json") "malformed input"
        assertTrue (errors.Length >= 1) "at least one error"
        assertContains "JSON" (List.head errors) "mentions JSON")

    suite "Configuration validation"

    test "every validation error is reported at once" (fun () ->
        let json = """{"minSizeBytes": -1, "threadCount": 0, "ageThresholdDays": -5}"""
        let errors = assertError (parse json) "three invalid values"
        assertEqual 3 errors.Length "all three errors reported together")

    test "thread count above 64 is rejected" (fun () ->
        let errors = assertError (parse """{"threadCount": 128}""") "excessive thread count"
        assertContains "threadCount" (List.head errors) "names the field")

    test "an unknown hash algorithm is rejected" (fun () ->
        let errors = assertError (parse """{"hashAlgorithm": "crc32"}""") "unsupported algorithm"
        assertContains "hashAlgorithm" (List.head errors) "names the field")

    test "sha256 and md5 are both accepted" (fun () ->
        assertEqual "sha256" (assertOk (parse """{"hashAlgorithm": "SHA256"}""") "sha256").HashAlgorithm "sha256 normalized"
        assertEqual "md5" (assertOk (parse """{"hashAlgorithm": "MD5"}""") "md5").HashAlgorithm "md5 normalized")

    suite "Configuration risk overrides"

    test "an override that raises severity is accepted" (fun () ->
        let json = """{"riskOverrides": {"Homebrew Cache": "Review"}}"""
        let c = assertOk (parse json) "raising override accepted"
        assertEqual (Some Review) (c.RiskOverrides.TryFind "Homebrew Cache") "override recorded")

    test "an override to NeverDelete is accepted" (fun () ->
        let json = """{"riskOverrides": {"Xcode DerivedData": "NeverDelete"}}"""
        let c = assertOk (parse json) "maximum severity accepted"
        assertEqual (Some NeverDelete) (c.RiskOverrides.TryFind "Xcode DerivedData") "override recorded")

    test "an unknown risk level name is rejected" (fun () ->
        let errors = assertError (parse """{"riskOverrides": {"Some Rule": "Whatever"}}""") "bad level"
        assertContains "Whatever" (List.head errors) "names the offending value")
```

Note: the *lowering* rejection is enforced in `RiskEngine` (Task 8), because only there is a rule's declared risk known. `Configuration` validates that the level parses; `RiskEngine` validates the direction. Task 8 tests that.

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestConfiguration.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestConfiguration.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/Configuration.fsx`.

- [ ] **Step 4: Write Configuration.fsx**

Create `src/Configuration.fsx`:

```fsharp
(**
    JSON configuration loading, validation, and merging over defaults.

    Validation accumulates every error rather than stopping at the first, so a
    user with three mistakes fixes them in one pass instead of three runs.

    Parsing is done against JsonDocument rather than deserializing into an F#
    record directly: System.Text.Json cannot handle F# discriminated unions and
    silently accepts unknown fields, both of which we want to control here.
*)
module Configuration

open System
open System.Text.Json

open Types
open Utilities

/// User-supplied settings, merged over `defaults`.
type Config =
    { /// Absolute paths excluded from scanning entirely.
      IgnorePaths: string list
      /// Lowercase extensions (with leading dot) excluded from results.
      IgnoreExtensions: string list
      /// Files below this size are ignored for hashing and large-file reporting.
      MinSizeBytes: int64
      /// Items younger than this are not considered stale.
      AgeThresholdDays: int
      /// Traversal parallelism.
      ThreadCount: int
      /// "sha256" or "md5".
      HashAlgorithm: string
      /// Rule name to a replacement risk level. May only raise severity;
      /// the direction is enforced by RiskEngine, which knows declared risks.
      RiskOverrides: Map<string, RiskLevel> }

/// Settings used when no configuration file is present.
let defaults: Config =
    { IgnorePaths = []
      IgnoreExtensions = []
      MinSizeBytes = 1_048_576L
      AgeThresholdDays = 30
      ThreadCount = min Environment.ProcessorCount 8
      HashAlgorithm = "sha256"
      RiskOverrides = Map.empty }

/// The conventional configuration location when `--config` is not supplied.
let defaultPath () : string =
    IO.Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
        ".config", "diskcleanupadvisor", "config.json")

/// Reads an optional property, returning None when absent or null.
let private tryProperty (root: JsonElement) (name: string) : JsonElement option =
    match root.TryGetProperty name with
    | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
    | _ -> None

/// Reads an optional string array.
let private readStringArray (root: JsonElement) (name: string) : Result<string list option, string> =
    match tryProperty root name with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Array ->
        value.EnumerateArray()
        |> Seq.map (fun e -> e.GetString())
        |> Seq.filter (isNull >> not)
        |> Seq.toList
        |> Some
        |> Ok
    | Some _ -> Error (sprintf "%s must be an array of strings" name)

/// Reads an optional integer.
let private readInt (root: JsonElement) (name: string) : Result<int option, string> =
    match tryProperty root name with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Number ->
        match value.TryGetInt32() with
        | true, n -> Ok (Some n)
        | _ -> Error (sprintf "%s must be a 32-bit integer" name)
    | Some _ -> Error (sprintf "%s must be a number" name)

/// Reads an optional 64-bit integer.
let private readInt64 (root: JsonElement) (name: string) : Result<int64 option, string> =
    match tryProperty root name with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Number ->
        match value.TryGetInt64() with
        | true, n -> Ok (Some n)
        | _ -> Error (sprintf "%s must be a 64-bit integer" name)
    | Some _ -> Error (sprintf "%s must be a number" name)

/// Reads an optional string.
let private readString (root: JsonElement) (name: string) : Result<string option, string> =
    match tryProperty root name with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.String -> Ok (Some (value.GetString()))
    | Some _ -> Error (sprintf "%s must be a string" name)

/// Reads the riskOverrides object, parsing each level name.
let private readOverrides (root: JsonElement) : Result<Map<string, RiskLevel> option, string list> =
    match tryProperty root "riskOverrides" with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Object ->
        let parsed =
            value.EnumerateObject()
            |> Seq.map (fun prop ->
                if prop.Value.ValueKind <> JsonValueKind.String then
                    Error (sprintf "riskOverrides.%s must be a string" prop.Name)
                else
                    match RiskLevel.parse (prop.Value.GetString()) with
                    | Ok level -> Ok (prop.Name, level)
                    | Error message -> Error (sprintf "riskOverrides.%s: %s" prop.Name message))
            |> Seq.toList
        let errors = parsed |> List.choose (function Error e -> Some e | Ok _ -> None)
        if List.isEmpty errors then
            Ok (Some (parsed |> List.choose (function Ok kv -> Some kv | Error _ -> None) |> Map.ofList))
        else Error errors
    | Some _ -> Error [ "riskOverrides must be an object" ]

/// Validates a fully-merged configuration, returning every problem found.
let private validate (config: Config) : Result<Config, string list> =
    let errors =
        [ if config.MinSizeBytes < 0L then
            yield sprintf "minSizeBytes must be zero or greater (got %d)" config.MinSizeBytes
          if config.AgeThresholdDays < 0 then
            yield sprintf "ageThresholdDays must be zero or greater (got %d)" config.AgeThresholdDays
          if config.ThreadCount < 1 then
            yield sprintf "threadCount must be at least 1 (got %d)" config.ThreadCount
          elif config.ThreadCount > 64 then
            yield sprintf "threadCount must be 64 or fewer (got %d)" config.ThreadCount
          if config.HashAlgorithm <> "sha256" && config.HashAlgorithm <> "md5" then
            yield sprintf "hashAlgorithm must be 'sha256' or 'md5' (got '%s')" config.HashAlgorithm ]
    if List.isEmpty errors then Ok config else Error errors

/// Parses configuration JSON, merging supplied values over the defaults.
/// Returns every validation error at once.
let parse (json: string) : Result<Config, string list> =
    let documentResult =
        try Ok (JsonDocument.Parse json)
        with :? JsonException as ex -> Error [ sprintf "invalid JSON: %s" ex.Message ]

    match documentResult with
    | Error errors -> Error errors
    | Ok document ->
        use document = document
        let root = document.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            Error [ "configuration root must be a JSON object" ]
        else
            // Collect field-level read errors before validating semantics.
            let ignorePaths = readStringArray root "ignorePaths"
            let ignoreExtensions = readStringArray root "ignoreExtensions"
            let minSize = readInt64 root "minSizeBytes"
            let ageThreshold = readInt root "ageThresholdDays"
            let threadCount = readInt root "threadCount"
            let hashAlgorithm = readString root "hashAlgorithm"
            let overrides = readOverrides root

            let readErrors =
                [ match ignorePaths with Error e -> yield e | _ -> ()
                  match ignoreExtensions with Error e -> yield e | _ -> ()
                  match minSize with Error e -> yield e | _ -> ()
                  match ageThreshold with Error e -> yield e | _ -> ()
                  match threadCount with Error e -> yield e | _ -> ()
                  match hashAlgorithm with Error e -> yield e | _ -> ()
                  match overrides with Error es -> yield! es | _ -> () ]

            if not (List.isEmpty readErrors) then Error readErrors
            else
                let pick result fallback =
                    match result with
                    | Ok (Some v) -> v
                    | _ -> fallback

                let merged =
                    { IgnorePaths =
                        pick ignorePaths defaults.IgnorePaths
                        |> List.map (Utilities.Path.expandHome >> Utilities.Path.normalize)
                      IgnoreExtensions =
                        pick ignoreExtensions defaults.IgnoreExtensions
                        |> List.map (fun e ->
                            let lower = e.Trim().ToLowerInvariant()
                            if lower.StartsWith "." then lower else "." + lower)
                      MinSizeBytes = pick minSize defaults.MinSizeBytes
                      AgeThresholdDays = pick ageThreshold defaults.AgeThresholdDays
                      ThreadCount = pick threadCount defaults.ThreadCount
                      HashAlgorithm = (pick hashAlgorithm defaults.HashAlgorithm).Trim().ToLowerInvariant()
                      RiskOverrides = pick overrides defaults.RiskOverrides }

                validate merged

/// Loads configuration from an explicit path, the conventional path, or
/// falls back to defaults when neither exists.
///
/// An explicitly requested path that is missing is an error; a missing
/// conventional path is not.
let load (fs: Platform.FileSystemOps) (explicitPath: string option) : Result<Config, string list> =
    let readFile path =
        try Ok (IO.File.ReadAllText path)
        with ex -> Error [ sprintf "cannot read %s: %s" path ex.Message ]

    match explicitPath with
    | Some path ->
        if not (fs.Exists path) then Error [ sprintf "configuration file not found: %s" path ]
        else readFile path |> Result.bind parse
    | None ->
        let conventional = defaultPath ()
        if fs.Exists conventional then readFile conventional |> Result.bind parse
        else Ok defaults
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 49 tests total.

- [ ] **Step 6: Verify error accumulation by hand**

```bash
cat > /tmp/cfgprobe.fsx <<'EOF'
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
match Configuration.parse """{"minSizeBytes": -1, "threadCount": 0, "hashAlgorithm": "crc32"}""" with
| Ok _ -> printfn "UNEXPECTED: should have failed"
| Error errors -> errors |> List.iter (printfn "  - %s")
EOF
dotnet fsi /tmp/cfgprobe.fsx && rm /tmp/cfgprobe.fsx
```

Expected: exactly three bullet lines, one per invalid field. If only one appears, `validate` is short-circuiting and must be fixed.

- [ ] **Step 7: Commit**

```bash
git add src/Configuration.fsx tests/TestConfiguration.fsx tests/Tests.fsx
git commit -m "feat: add JSON configuration with accumulating validation

Parses against JsonDocument rather than deserializing to an F# record,
since System.Text.Json cannot handle discriminated unions. Every
validation error is reported at once so users fix them in one pass."
```

---

### Task 7: Rule catalog

**Files:**
- Create: `src/Rules.fsx`
- Create: `tests/TestRules.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`, `Platform`
- Produces:
  - `Rules.Rule` (record — full definition below)
  - `Rules.ResolvedRule` = `{ Rule: Rule; Root: string }`
  - `Rules.catalog : Rule list`
  - `Rules.forPlatform : Types.Platform -> Rule list`
  - `Rules.resolveAll : Platform.FileSystemOps -> Types.Platform -> ResolvedRule list`
  - `Rules.byPrecedence : ResolvedRule list -> ResolvedRule list`

**Precedence rule:** most-specific-first by resolved root path **length descending**, then declaration order. This is what lets `Containers/*/Data/Library/Caches` (Safe) win over `Containers/**` (Review).

- [ ] **Step 1: Write the failing tests**

Create `tests/TestRules.fsx`:

```fsharp
open System
open TestFramework
open Types
open Rules

let run () =
    suite "Rules.catalog"

    test "every rule has a non-empty name, description, and reason" (fun () ->
        for rule in catalog do
            assertTrue (rule.Name.Length > 0) "name is non-empty"
            assertTrue (rule.Description.Length > 0) (sprintf "%s has a description" rule.Name)
            assertTrue (rule.Reason.Length > 0) (sprintf "%s has a reason" rule.Name)
            assertTrue (rule.Examples.Length > 0) (sprintf "%s has at least one example" rule.Name))

    test "rule names are unique" (fun () ->
        let names = catalog |> List.map (fun r -> r.Name)
        assertEqual names.Length (names |> List.distinct |> List.length) "no duplicate rule names")

    test "every rule declares at least one platform" (fun () ->
        for rule in catalog do
            assertFalse (Set.isEmpty rule.Platforms) (sprintf "%s declares a platform" rule.Name))

    test "only Safe rules may assert SafeToDelete" (fun () ->
        // A rule claiming deletability at any other risk level is a
        // contradiction the risk engine would have to resolve at runtime.
        for rule in catalog do
            if rule.SafeToDelete then
                assertEqual Safe rule.Risk (sprintf "%s asserts SafeToDelete so must be Safe" rule.Name))

    test "no rule targets a protected user data location" (fun () ->
        let forbidden = [ "Documents"; "Desktop"; "Pictures"; "Movies"; "Music" ]
        for rule in catalog do
            if rule.SafeToDelete then
                for segment in forbidden do
                    let root = match rule.Root with
                               | HomeRelative r -> r
                               | Absolute a -> a
                               | EnvRelative (_, r) -> r
                    assertFalse (root.Contains segment)
                        (sprintf "%s must not target %s" rule.Name segment))

    suite "Rules.forPlatform"

    test "macOS rules are returned for macOS" (fun () ->
        let macRules = forPlatform MacOS
        assertTrue (macRules.Length >= 20) "macOS catalog is populated"
        for rule in macRules do
            assertTrue (rule.Platforms.Contains MacOS) (sprintf "%s applies to macOS" rule.Name))

    test "Linux and Windows have seeded catalogs" (fun () ->
        assertTrue ((forPlatform Linux).Length >= 4) "Linux seeded"
        assertTrue ((forPlatform Windows).Length >= 4) "Windows seeded")

    suite "Rules.byPrecedence"

    test "more specific roots sort before less specific ones" (fun () ->
        let specific =
            { Rule = catalog |> List.head; Root = "/a/b/c/d/e" }
        let general =
            { Rule = catalog |> List.head; Root = "/a" }
        let ordered = byPrecedence [ general; specific ]
        assertEqual "/a/b/c/d/e" (List.head ordered).Root "longest root first")

    test "the Containers cache rule outranks the Containers catch-all" (fun () ->
        // This is the specific requirement from the spec: only Data/Library/Caches
        // and Data/tmp inside ~/Library/Containers may be Safe; everything else
        // must fall through to Review.
        let macRules = forPlatform MacOS
        let cacheRule = macRules |> List.tryFind (fun r -> r.Name = "Container Caches")
        let catchAll = macRules |> List.tryFind (fun r -> r.Name = "Container Data")
        assertTrue cacheRule.IsSome "Container Caches rule exists"
        assertTrue catchAll.IsSome "Container Data catch-all exists"
        assertEqual Safe cacheRule.Value.Risk "container caches are Safe"
        assertEqual Review catchAll.Value.Risk "other container data is Review"
        assertFalse catchAll.Value.SafeToDelete "catch-all is not deletable")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestRules.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestRules.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/Rules.fsx`.

- [ ] **Step 4: Write Rules.fsx**

Create `src/Rules.fsx`. The catalog is data; the engine never branches on a specific rule.

```fsharp
(**
    The rule catalog.

    A rule is a pure data description of a location, what lives there, and how
    dangerous it is to remove. The scanner is parameterized over `Rule list` and
    contains no rule-specific logic, so **adding a rule means appending a record
    here** — no other file changes.

    Two invariants are enforced by tests rather than by types, because they
    concern combinations of fields:
      * Only a rule at risk level `Safe` may set `SafeToDelete = true`.
      * No `SafeToDelete` rule may target a user-data directory.
*)
module Rules

open System

open Types
open Utilities

/// A declarative description of a cleanup target.
type Rule =
    { /// Unique, stable identifier. Referenced by config riskOverrides.
      Name: string
      /// Which operating systems this rule applies to.
      Platforms: Set<Types.Platform>
      /// Where to look, resolved against the environment at startup.
      Root: PathSpec
      /// Globs relative to Root. Empty means "everything under Root".
      Include: string list
      /// Globs relative to Root that are excluded from matching.
      Exclude: string list
      /// How dangerous removal is.
      Risk: RiskLevel
      /// How the item can be recovered after removal.
      Recoverability: Recoverability
      /// How matched items should be removed.
      Strategy: DeleteStrategy
      /// Items younger than this are not candidates. None means no age floor.
      MinAge: TimeSpan option
      /// Items smaller than this are not candidates. None means no size floor.
      MinSize: int64 option
      /// Whether this rule permits automatic deletion. Only ever true at Safe.
      SafeToDelete: bool
      /// What this location contains.
      Description: string
      /// Why it is (or is not) disposable.
      Reason: string
      /// Rough expected reclaim, for report prioritization.
      EstimatedBenefit: string
      /// Concrete example paths, shown in verbose reports.
      Examples: string list
      /// Report grouping.
      Category: Category }

/// A rule whose root resolved to a real, existing path on this machine.
type ResolvedRule = { Rule: Rule; Root: string }

/// Shorthand for a rule with the common defaults filled in, so each catalog
/// entry states only what distinguishes it.
let private rule name platforms root category =
    { Name = name
      Platforms = Set.ofList platforms
      Root = root
      Include = []
      Exclude = []
      Risk = Review
      Recoverability = Unrecoverable
      Strategy = MatchingFiles
      MinAge = None
      MinSize = None
      SafeToDelete = false
      Description = ""
      Reason = ""
      EstimatedBenefit = ""
      Examples = []
      Category = category }

/// Marks a rule as safely deletable. Sets both fields together so the
/// SafeToDelete/Risk pair can never drift apart.
let private safe (r: Rule) = { r with Risk = Safe; SafeToDelete = true }

// ---------------------------------------------------------------------------
// macOS catalog — complete
// ---------------------------------------------------------------------------

let private macOsRules: Rule list =
    [ { rule "User Caches" [ MacOS ] (HomeRelative "Library/Caches") Caches with
          Strategy = ContentsOnly
          Recoverability = Regenerated
          Description = "Application caches for the current user."
          Reason = "Applications regenerate these on demand. Removing them costs a one-time slowdown, nothing more."
          EstimatedBenefit = "1-20 GB"
          Examples = [ "~/Library/Caches/com.apple.Safari"; "~/Library/Caches/JetBrains" ] } |> safe

      { rule "User Logs" [ MacOS ] (HomeRelative "Library/Logs") Logs with
          Strategy = ContentsOnly
          Recoverability = Regenerated
          MinAge = Some (TimeSpan.FromDays 7.0)
          Description = "Application log files for the current user."
          Reason = "Diagnostic output. Only useful while investigating a recent problem."
          EstimatedBenefit = "100 MB - 2 GB"
          Examples = [ "~/Library/Logs/DiagnosticReports" ] } |> safe

      { rule "Crash Reports" [ MacOS ] (HomeRelative "Library/Application Support/CrashReporter") CrashReports with
          Strategy = ContentsOnly
          Recoverability = Unrecoverable
          MinAge = Some (TimeSpan.FromDays 30.0)
          Description = "Saved crash reports."
          Reason = "Useful only when actively diagnosing a crash. Older reports refer to versions no longer installed."
          EstimatedBenefit = "10-500 MB"
          Examples = [ "~/Library/Application Support/CrashReporter/*.plist" ] } |> safe

      { rule "Xcode DerivedData" [ MacOS ] (HomeRelative "Library/Developer/Xcode/DerivedData") DeveloperArtifacts with
          Strategy = ContentsOnly
          Recoverability = Regenerated
          Description = "Xcode build intermediates, indexes, and module caches."
          Reason = "Fully regenerated by a rebuild. Frequently the single largest reclaimable directory on a developer Mac. The directory itself is kept because Xcode expects it to exist."
          EstimatedBenefit = "5-50 GB"
          Examples = [ "~/Library/Developer/Xcode/DerivedData/MyApp-abc123" ] } |> safe

      { rule "Xcode Archives" [ MacOS ] (HomeRelative "Library/Developer/Xcode/Archives") DeveloperArtifacts with
          Risk = Review
          Recoverability = Unrecoverable
          MinAge = Some (TimeSpan.FromDays 90.0)
          Description = "Archived application builds produced for distribution."
          Reason = "These contain the dSYM files needed to symbolicate crash reports from released builds. Removing an archive for a version still in the wild makes its crash reports unreadable. Reviewed, never automatic."
          EstimatedBenefit = "1-20 GB"
          Examples = [ "~/Library/Developer/Xcode/Archives/2024-01-15" ] }

      { rule "Xcode iOS DeviceSupport" [ MacOS ] (HomeRelative "Library/Developer/Xcode/iOS DeviceSupport") DeveloperArtifacts with
          Strategy = ContentsOnly
          Recoverability = Regenerated
          MinAge = Some (TimeSpan.FromDays 60.0)
          Description = "Debug symbols cached per connected iOS device and OS version."
          Reason = "Regenerated when a device running that OS version is next connected. Old iOS versions accumulate indefinitely."
          EstimatedBenefit = "2-30 GB"
          Examples = [ "~/Library/Developer/Xcode/iOS DeviceSupport/17.2 (21C62)" ] } |> safe

      { rule "CoreSimulator Caches" [ MacOS ] (HomeRelative "Library/Developer/CoreSimulator/Caches") DeveloperArtifacts with
          Strategy = ContentsOnly
          Recoverability = Regenerated
          Description = "iOS Simulator runtime caches."
          Reason = "Regenerated by the simulator on next launch."
          EstimatedBenefit = "500 MB - 5 GB"
          Examples = [ "~/Library/Developer/CoreSimulator/Caches/dyld" ] } |> safe

      { rule "CoreSimulator Devices" [ MacOS ] (HomeRelative "Library/Developer/CoreSimulator/Devices") DeveloperArtifacts with
          Risk = Review
          Recoverability = Unrecoverable
          Description = "Simulated device containers, including their installed apps and data."
          Reason = "These hold app state a developer may be mid-test against. Removal is safe in principle but destroys simulator state without warning, so it is surfaced for review rather than deleted."
          EstimatedBenefit = "1-20 GB"
          Examples = [ "~/Library/Developer/CoreSimulator/Devices/ABC-123" ] }

      { rule "Container Caches" [ MacOS ] (HomeRelative "Library/Containers") ContainerData with
          Include = [ "*/Data/Library/Caches/**"; "*/Data/tmp/**" ]
          Strategy = MatchingFiles
          Recoverability = Regenerated
          Description = "Cache and temporary directories inside sandboxed application containers."
          Reason = "Only these two subpaths within a container are regenerable. Everything else in a container is application state."
          EstimatedBenefit = "500 MB - 10 GB"
          Examples = [ "~/Library/Containers/com.apple.Notes/Data/Library/Caches" ] } |> safe

      { rule "Container Data" [ MacOS ] (HomeRelative "Library/Containers") ContainerData with
          Risk = Review
          Recoverability = Unrecoverable
          Description = "All other data inside sandboxed application containers."
          Reason = "Sandboxed applications store documents, preferences, and databases here. This is user data and is never removed automatically."
          EstimatedBenefit = "reported only"
          Examples = [ "~/Library/Containers/com.apple.Notes/Data/Documents" ] }

      { rule "Trash" [ MacOS ] (HomeRelative ".Trash") TrashContents with
          Risk = Review
          Recoverability = Unrecoverable
          Description = "Items already in the Trash."
          Reason = "This space is not reclaimed until the Trash is emptied. This tool reports the amount but will not empty the Trash, because doing so is the one action that makes its own deletions irreversible."
          EstimatedBenefit = "reported only"
          Examples = [ "~/.Trash" ] }

      { rule "Safari Cache" [ MacOS ] (HomeRelative "Library/Caches/com.apple.Safari") BrowserCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          Description = "Safari's on-disk web cache."
          Reason = "Re-downloaded as you browse."
          EstimatedBenefit = "100 MB - 2 GB"
          Examples = [ "~/Library/Caches/com.apple.Safari/WebKitCache" ] } |> safe

      { rule "Chrome Cache" [ MacOS ] (HomeRelative "Library/Caches/Google/Chrome") BrowserCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          Description = "Google Chrome's on-disk web cache."
          Reason = "Re-downloaded as you browse. Does not include profiles, history, or passwords, which live elsewhere."
          EstimatedBenefit = "200 MB - 3 GB"
          Examples = [ "~/Library/Caches/Google/Chrome/Default/Cache" ] } |> safe

      { rule "Firefox Cache" [ MacOS ] (HomeRelative "Library/Caches/Firefox") BrowserCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          Description = "Firefox's on-disk web cache."
          Reason = "Re-downloaded as you browse."
          EstimatedBenefit = "100 MB - 2 GB"
          Examples = [ "~/Library/Caches/Firefox/Profiles" ] } |> safe

      { rule "NuGet Package Cache" [ MacOS; Linux ] (HomeRelative ".nuget/packages") PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          MinAge = Some (TimeSpan.FromDays 30.0)
          Description = ".NET package cache."
          Reason = "Re-downloaded by `dotnet restore`. Grows without bound across projects and framework versions."
          EstimatedBenefit = "1-15 GB"
          Examples = [ "~/.nuget/packages/newtonsoft.json" ] } |> safe

      { rule "Homebrew Cache" [ MacOS ] (HomeRelative "Library/Caches/Homebrew") PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          Description = "Downloaded Homebrew bottles and source archives."
          Reason = "Re-downloaded on demand. `brew cleanup` does the same thing."
          EstimatedBenefit = "500 MB - 10 GB"
          Examples = [ "~/Library/Caches/Homebrew/downloads" ] } |> safe

      { rule "npm Cache" [ MacOS; Linux ] (HomeRelative ".npm/_cacache") PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          Description = "npm's content-addressable package cache."
          Reason = "Re-downloaded by `npm install`. Equivalent to `npm cache clean --force`."
          EstimatedBenefit = "500 MB - 5 GB"
          Examples = [ "~/.npm/_cacache/content-v2" ] } |> safe

      { rule "pnpm Store" [ MacOS ] (HomeRelative "Library/pnpm/store") PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          MinAge = Some (TimeSpan.FromDays 30.0)
          Description = "pnpm's global content-addressable store."
          Reason = "Re-downloaded on install. Note that pnpm hard-links from this store into project node_modules, so removing it forces a re-fetch for existing projects too."
          EstimatedBenefit = "1-10 GB"
          Examples = [ "~/Library/pnpm/store/v3" ] } |> safe

      { rule "Yarn Cache" [ MacOS; Linux ] (HomeRelative ".yarn/cache") PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          Description = "Yarn's package cache."
          Reason = "Re-downloaded by `yarn install`."
          EstimatedBenefit = "200 MB - 3 GB"
          Examples = [ "~/.yarn/cache" ] } |> safe

      { rule "pip Cache" [ MacOS; Linux ] (HomeRelative "Library/Caches/pip") PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          Description = "Python wheel and HTTP cache."
          Reason = "Re-downloaded by `pip install`. Equivalent to `pip cache purge`."
          EstimatedBenefit = "100 MB - 2 GB"
          Examples = [ "~/Library/Caches/pip/wheels" ] } |> safe

      { rule "Gradle Cache" [ MacOS; Linux ] (HomeRelative ".gradle/caches") PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          MinAge = Some (TimeSpan.FromDays 30.0)
          Description = "Gradle dependency and build cache."
          Reason = "Re-downloaded and rebuilt on next build."
          EstimatedBenefit = "1-20 GB"
          Examples = [ "~/.gradle/caches/modules-2" ] } |> safe

      { rule "Maven Repository" [ MacOS; Linux ] (HomeRelative ".m2/repository") PackageManagerCache with
          Risk = ProbablySafe
          Recoverability = Redownloadable
          MinAge = Some (TimeSpan.FromDays 60.0)
          Description = "Maven's local artifact repository."
          Reason = "Normally re-downloadable, but it can also contain artifacts installed locally via `mvn install` that exist nowhere else. Rated ProbablySafe rather than Safe for that reason."
          EstimatedBenefit = "1-10 GB"
          Examples = [ "~/.m2/repository/org/apache" ] }

      { rule "Terraform Plugin Cache" [ MacOS; Linux ] (HomeRelative ".terraform.d/plugin-cache") PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          Description = "Cached Terraform provider binaries."
          Reason = "Re-downloaded by `terraform init`."
          EstimatedBenefit = "200 MB - 3 GB"
          Examples = [ "~/.terraform.d/plugin-cache/registry.terraform.io" ] } |> safe

      { rule "Docker Data" [ MacOS ] (HomeRelative "Library/Containers/com.docker.docker/Data") VirtualMachines with
          Risk = Review
          Recoverability = Unrecoverable
          Description = "Docker Desktop's virtual machine disk image, holding all images, volumes, and containers."
          Reason = "This is a single large sparse file containing every Docker volume, including databases a developer may depend on. Use `docker system prune` instead, which understands what is safe to remove. Reported, never deleted."
          EstimatedBenefit = "reported only"
          Examples = [ "~/Library/Containers/com.docker.docker/Data/vms/0/data" ] }

      { rule "Podman Machine Images" [ MacOS ] (HomeRelative ".local/share/containers/podman") VirtualMachines with
          Risk = Review
          Recoverability = Unrecoverable
          Description = "Podman virtual machine images."
          Reason = "Contains container volumes and machine state. Use `podman system prune` instead."
          EstimatedBenefit = "reported only"
          Examples = [ "~/.local/share/containers/podman/machine" ] }

      { rule "Python Bytecode Caches" [ MacOS; Linux ] (HomeRelative "") DeveloperArtifacts with
          Include = [ "**/__pycache__/**" ]
          Strategy = MatchingFiles
          Recoverability = Regenerated
          Description = "Compiled Python bytecode."
          Reason = "Regenerated automatically on next import."
          EstimatedBenefit = "50-500 MB"
          Examples = [ "~/Projects/app/__pycache__" ] } |> safe

      { rule "node_modules Directories" [ MacOS; Linux ] (HomeRelative "") DeveloperArtifacts with
          Include = [ "**/node_modules/**" ]
          Risk = Review
          Recoverability = Redownloadable
          Description = "Installed JavaScript dependencies, per project."
          Reason = "Reinstallable via the project's lockfile, but only if the project is still wanted and the lockfile is intact. These contain enormous file counts, so they are reported as a single aggregate per project rather than enumerated file by file."
          EstimatedBenefit = "reported only"
          Examples = [ "~/Projects/app/node_modules" ] }

      { rule "Old Disk Images" [ MacOS ] (HomeRelative "Downloads") DiskImages with
          Include = [ "*.dmg"; "*.iso"; "*.pkg" ]
          Risk = Review
          Recoverability = Redownloadable
          MinAge = Some (TimeSpan.FromDays 60.0)
          Description = "Installer disk images and packages in Downloads."
          Reason = "Usually re-downloadable from the vendor, but Downloads is user territory and may hold licensed installers that are no longer available. Reviewed, never automatic."
          EstimatedBenefit = "1-20 GB"
          Examples = [ "~/Downloads/Xcode_15.dmg" ] }

      { rule "Old Archives" [ MacOS; Linux ] (HomeRelative "Downloads") Archives with
          Include = [ "*.zip"; "*.tar"; "*.tar.gz"; "*.tgz"; "*.rar"; "*.7z" ]
          Risk = Review
          Recoverability = Unrecoverable
          MinAge = Some (TimeSpan.FromDays 90.0)
          Description = "Compressed archives in Downloads."
          Reason = "Often already extracted and redundant, but the tool cannot tell which. Reviewed, never automatic."
          EstimatedBenefit = "500 MB - 10 GB"
          Examples = [ "~/Downloads/project-backup.zip" ] } ]

// ---------------------------------------------------------------------------
// Linux catalog — seeded
// ---------------------------------------------------------------------------

let private linuxRules: Rule list =
    [ { rule "System Temp" [ Linux ] (Absolute "/tmp") TemporaryFiles with
          Strategy = MatchingFiles
          Recoverability = Regenerated
          MinAge = Some (TimeSpan.FromDays 7.0)
          Description = "System temporary directory."
          Reason = "Cleared on reboot by most distributions. Files older than a week are abandoned."
          EstimatedBenefit = "100 MB - 5 GB"
          Examples = [ "/tmp/build-abc123" ] } |> safe

      { rule "Persistent Temp" [ Linux ] (Absolute "/var/tmp") TemporaryFiles with
          Risk = Review
          Recoverability = Unrecoverable
          MinAge = Some (TimeSpan.FromDays 30.0)
          Description = "Temporary files that survive reboot."
          Reason = "Unlike /tmp, this is expected to persist, so long-running jobs legitimately keep state here."
          EstimatedBenefit = "reported only"
          Examples = [ "/var/tmp/systemd-private-abc" ] }

      { rule "XDG User Cache" [ Linux ] (HomeRelative ".cache") Caches with
          Strategy = ContentsOnly
          Recoverability = Regenerated
          Description = "Per-user application cache directory."
          Reason = "Applications regenerate these on demand."
          EstimatedBenefit = "500 MB - 10 GB"
          Examples = [ "~/.cache/mozilla" ] } |> safe

      { rule "Flatpak Unused Runtimes" [ Linux ] (HomeRelative ".local/share/flatpak") PackageManagerCache with
          Risk = Review
          Recoverability = Redownloadable
          Description = "Installed Flatpak runtimes and applications."
          Reason = "Use `flatpak uninstall --unused`, which knows which runtimes are still referenced. Reported only."
          EstimatedBenefit = "reported only"
          Examples = [ "~/.local/share/flatpak/runtime" ] } ]

// ---------------------------------------------------------------------------
// Windows catalog — seeded
// ---------------------------------------------------------------------------

let private windowsRules: Rule list =
    [ { rule "User Temp" [ Windows ] (EnvRelative("TEMP", "")) TemporaryFiles with
          Strategy = MatchingFiles
          Recoverability = Regenerated
          MinAge = Some (TimeSpan.FromDays 7.0)
          Description = "Per-user temporary directory."
          Reason = "Abandoned by the applications that created them."
          EstimatedBenefit = "500 MB - 5 GB"
          Examples = [ "%TEMP%\\nsy1234.tmp" ] } |> safe

      { rule "Windows Update Cache" [ Windows ] (Absolute "C:\\Windows\\SoftwareDistribution\\Download") Caches with
          Risk = ProbablySafe
          Recoverability = Redownloadable
          MinAge = Some (TimeSpan.FromDays 30.0)
          Description = "Downloaded Windows Update payloads."
          Reason = "Re-downloaded if needed, but removing them mid-update interrupts a pending install."
          EstimatedBenefit = "500 MB - 8 GB"
          Examples = [ "C:\\Windows\\SoftwareDistribution\\Download" ] }

      { rule "NuGet Package Cache (Windows)" [ Windows ] (EnvRelative("USERPROFILE", ".nuget\\packages")) PackageManagerCache with
          Strategy = ContentsOnly
          Recoverability = Redownloadable
          MinAge = Some (TimeSpan.FromDays 30.0)
          Description = ".NET package cache."
          Reason = "Re-downloaded by `dotnet restore`."
          EstimatedBenefit = "1-15 GB"
          Examples = [ "%USERPROFILE%\\.nuget\\packages" ] } |> safe

      { rule "Recycle Bin" [ Windows ] (Absolute "C:\\$Recycle.Bin") TrashContents with
          Risk = Review
          Recoverability = Unrecoverable
          Description = "Items already in the Recycle Bin."
          Reason = "This space is not reclaimed until the Recycle Bin is emptied, which this tool will not do."
          EstimatedBenefit = "reported only"
          Examples = [ "C:\\$Recycle.Bin" ] } ]

/// Every rule across every platform.
let catalog: Rule list = macOsRules @ linuxRules @ windowsRules

/// Rules applicable to a given platform.
let forPlatform (platform: Types.Platform) : Rule list =
    catalog |> List.filter (fun r -> r.Platforms.Contains platform)

/// Orders rules most-specific-first: longer resolved roots win, then
/// declaration order. This is what lets a narrow rule inside a broad one
/// take precedence — the Containers cache rule over the Containers catch-all.
let byPrecedence (rules: ResolvedRule list) : ResolvedRule list =
    rules |> List.sortBy (fun r -> -r.Root.Length)

/// Resolves every applicable rule against this machine, dropping those whose
/// root does not exist. A missing root simply means the tool that creates it
/// is not installed.
let resolveAll (fs: Platform.FileSystemOps) (platform: Types.Platform) : ResolvedRule list =
    forPlatform platform
    |> List.choose (fun r ->
        match Platform.resolve r.Root with
        | Some root when fs.Exists root -> Some { Rule = r; Root = root }
        | _ -> None)
    |> byPrecedence
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 58 tests total.

- [ ] **Step 6: Inspect which rules resolve on this machine**

```bash
cat > /tmp/ruleprobe.fsx <<'EOF'
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
let fs = Platform.FileSystem.real
let resolved = Rules.resolveAll fs (Platform.current ())
printfn "%d of %d rules resolved on this machine:" resolved.Length (Rules.forPlatform (Platform.current ())).Length
for r in resolved do printfn "  %-32s %s" r.Rule.Name r.Root
EOF
dotnet fsi /tmp/ruleprobe.fsx && rm /tmp/ruleprobe.fsx
```

Expected: a list of rules whose roots exist. On a machine with Xcode and .NET installed, `Xcode DerivedData` and `NuGet Package Cache` should appear. Rules for uninstalled tooling are correctly absent. Confirm the roots printed are the ones you expect before proceeding — this is the last checkpoint before the risk engine consumes them.

- [ ] **Step 7: Commit**

```bash
git add src/Rules.fsx tests/TestRules.fsx tests/Tests.fsx
git commit -m "feat: add rule catalog with macOS complete, Linux/Windows seeded

Rules are pure data; the scanner is parameterized over the list and
contains no rule-specific logic, so adding a rule is appending a
record. The safe helper sets Risk and SafeToDelete together so the
pair cannot drift. Precedence is longest-root-first, which is what
lets the Containers cache rule outrank the Containers catch-all."
```

---

### Task 8: RiskEngine — the safety core

**This is the most important task in the plan.** Everything else is reporting. This task decides what may be destroyed.

**Files:**
- Create: `src/RiskEngine.fsx`
- Create: `tests/TestRiskEngine.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`, `Platform`, `Rules`, `Configuration`
- Produces:
  - `RiskEngine.denylist : unit -> string list`
  - `RiskEngine.isVetoed : string -> bool`
  - `RiskEngine.applyOverrides : Map<string, RiskLevel> -> Rule -> Result<Rule, string>`
  - `RiskEngine.DeletionCandidate` (**private constructor**)
  - `RiskEngine.candidatePath : DeletionCandidate -> string`
  - `RiskEngine.candidateSize : DeletionCandidate -> int64`
  - `RiskEngine.candidateRule : DeletionCandidate -> string`
  - `RiskEngine.Rejection` = `NotSafeToDelete | RiskTooHigh of RiskLevel | OutsideRuleRoot | Vetoed of string | IsSymlink`
  - `RiskEngine.evaluate : allowProbablySafe: bool -> ResolvedRule -> FileMeta -> Result<DeletionCandidate, Rejection>`

- [ ] **Step 1: Write the adversarial gate tests**

Create `tests/TestRiskEngine.fsx`. These tests are the safety guarantee — write them before any implementation.

```fsharp
open System
open TestFramework
open Types
open Rules
open RiskEngine

/// Builds a FileMeta for a path with sensible defaults.
let private meta path size =
    { Path = path
      SizeBytes = size
      LastAccess = DateTime.UtcNow.AddDays -10.0
      LastModified = DateTime.UtcNow.AddDays -10.0
      Created = DateTime.UtcNow.AddDays -100.0
      IsDirectory = false
      LinkTarget = None
      Owner = "testuser"
      Mode = "rw-r--r--" }

/// A Safe, deletable rule rooted at /testroot.
let private safeRule =
    { Rule =
        { Name = "Test Safe Rule"
          Platforms = Set.ofList [ MacOS; Linux; Windows ]
          Root = Absolute "/testroot"
          Include = []
          Exclude = []
          Risk = Safe
          Recoverability = Regenerated
          Strategy = ContentsOnly
          MinAge = None
          MinSize = None
          SafeToDelete = true
          Description = "test"
          Reason = "test"
          EstimatedBenefit = "test"
          Examples = [ "/testroot/x" ]
          Category = Caches }
      Root = "/testroot" }

let run () =
    suite "RiskEngine gate 1 — rule assertion"

    test "a Safe rule with SafeToDelete produces a candidate" (fun () ->
        let c = assertOk (evaluate false safeRule (meta "/testroot/file.tmp" 100L)) "safe rule accepts"
        assertEqual "/testroot/file.tmp" (candidatePath c) "path preserved"
        assertEqual 100L (candidateSize c) "size preserved")

    test "a rule without SafeToDelete is rejected" (fun () ->
        let rule = { safeRule with Rule = { safeRule.Rule with SafeToDelete = false } }
        assertEqual NotSafeToDelete (assertError (evaluate false rule (meta "/testroot/f" 1L)) "rejected") "NotSafeToDelete")

    test "a Review rule is rejected even when SafeToDelete is set" (fun () ->
        // Defence in depth: the catalog test forbids this combination, but the
        // engine must not trust that a malformed rule cannot reach it.
        let rule = { safeRule with Rule = { safeRule.Rule with Risk = Review } }
        assertEqual (RiskTooHigh Review) (assertError (evaluate false rule (meta "/testroot/f" 1L)) "rejected") "RiskTooHigh")

    test "ProbablySafe is rejected by default and accepted only with the flag" (fun () ->
        let rule = { safeRule with Rule = { safeRule.Rule with Risk = ProbablySafe } }
        assertEqual (RiskTooHigh ProbablySafe)
            (assertError (evaluate false rule (meta "/testroot/f" 1L)) "rejected without flag") "rejected by default"
        assertOk (evaluate true rule (meta "/testroot/f" 1L)) "accepted with allowProbablySafe" |> ignore)

    test "Dangerous and NeverDelete are rejected even with the force flag" (fun () ->
        for level in [ Dangerous; NeverDelete ] do
            let rule = { safeRule with Rule = { safeRule.Rule with Risk = level } }
            assertEqual (RiskTooHigh level)
                (assertError (evaluate true rule (meta "/testroot/f" 1L)) "rejected")
                (sprintf "%A is never deletable" level))

    suite "RiskEngine gate 2 — allowlist anchoring"

    test "a path outside the rule root is rejected" (fun () ->
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/elsewhere/file" 1L)) "outside root") "OutsideRuleRoot")

    test "the rule root itself is not a candidate" (fun () ->
        // Deleting the root would remove the very directory the rule describes.
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/testroot" 1L)) "root itself") "root is not its own descendant")

    test "a traversal escape from inside the root is rejected" (fun () ->
        // The attack: a path that textually starts with the root but resolves outside it.
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/testroot/../etc/passwd" 1L)) "escape rejected")
            "dot-dot escape rejected")

    test "a deeper traversal escape is rejected" (fun () ->
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/testroot/a/b/../../../etc/passwd" 1L)) "deep escape")
            "multi-level escape rejected")

    test "a sibling directory sharing a name prefix is rejected" (fun () ->
        // /testroot-backup must not be treated as inside /testroot.
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/testroot-backup/file" 1L)) "prefix collision")
            "prefix collision rejected")

    test "dot-dot that stays inside the root is accepted" (fun () ->
        assertOk (evaluate false safeRule (meta "/testroot/a/../b" 1L)) "inner dot-dot resolves inside" |> ignore)

    suite "RiskEngine gate 3 — denylist veto"

    test "protected user directories are vetoed" (fun () ->
        let home = Platform.home ()
        for folder in [ "Documents"; "Desktop"; "Pictures"; "Movies"; "Music" ] do
            let path = IO.Path.Combine(home, folder, "file.txt")
            assertTrue (isVetoed path) (sprintf "%s is vetoed" folder))

    test "system directories are vetoed" (fun () ->
        assertTrue (isVetoed "/System/Library/anything") "/System vetoed"
        assertTrue (isVetoed "/Applications/Xcode.app") "/Applications vetoed")

    test "any path inside a git repository is vetoed" (fun () ->
        // Deleting from inside .git can destroy unpushed commits.
        assertTrue (isVetoed "/anywhere/project/.git/objects/ab/cdef") ".git contents vetoed"
        assertTrue (isVetoed "/anywhere/project/.git") ".git itself vetoed")

    test "the veto overrides a Safe rule that would otherwise accept" (fun () ->
        // The critical case: a rule root that overlaps protected territory.
        let home = Platform.home ()
        let rule = { safeRule with
                       Rule = { safeRule.Rule with Root = Absolute home }
                       Root = home }
        let protectedPath = IO.Path.Combine(home, "Documents", "thesis.txt")
        match evaluate false rule (meta protectedPath 1L) with
        | Error (Vetoed _) -> ()
        | Error other -> assertTrue false (sprintf "expected Vetoed, got %A" other)
        | Ok _ -> assertTrue false "VETO FAILED — a protected path produced a candidate")

    test "the veto is not bypassed by the force flag" (fun () ->
        let home = Platform.home ()
        let rule = { safeRule with
                       Rule = { safeRule.Rule with Root = Absolute home }
                       Root = home }
        let protectedPath = IO.Path.Combine(home, "Desktop", "notes.txt")
        match evaluate true rule (meta protectedPath 1L) with
        | Error (Vetoed _) -> ()
        | _ -> assertTrue false "VETO FAILED — force bypassed the denylist")

    test "an ordinary path under a rule root is not vetoed" (fun () ->
        assertFalse (isVetoed "/testroot/cache/entry") "ordinary cache path allowed")

    suite "RiskEngine — symlinks"

    test "a symlink is never a candidate" (fun () ->
        // Following a link could relocate the target rather than the link.
        let link = { meta "/testroot/link" 0L with LinkTarget = Some "/elsewhere/real" }
        assertEqual IsSymlink (assertError (evaluate false safeRule link) "symlink rejected") "IsSymlink")

    suite "RiskEngine — override clamping"

    test "an override that raises risk is applied" (fun () ->
        let overrides = Map.ofList [ "Test Safe Rule", Review ]
        let updated = assertOk (applyOverrides overrides safeRule.Rule) "raising override applied"
        assertEqual Review updated.Risk "risk raised"
        assertFalse updated.SafeToDelete "raising risk also clears SafeToDelete")

    test "an override that lowers risk is an error, not a silent clamp" (fun () ->
        // Silently ignoring it would leave the user believing they had
        // loosened a restriction that in fact still applies.
        let reviewRule = { safeRule.Rule with Name = "Review Rule"; Risk = Review; SafeToDelete = false }
        let overrides = Map.ofList [ "Review Rule", Safe ]
        let message = assertError (applyOverrides overrides reviewRule) "lowering rejected"
        assertContains "Review Rule" message "names the rule"
        assertContains "lower" (message.ToLowerInvariant()) "explains the direction")

    test "an override naming an unknown rule is ignored without error" (fun () ->
        let overrides = Map.ofList [ "No Such Rule", Review ]
        let updated = assertOk (applyOverrides overrides safeRule.Rule) "unrelated override ignored"
        assertEqual Safe updated.Risk "unchanged")

    test "an override to the same level is accepted as a no-op" (fun () ->
        let overrides = Map.ofList [ "Test Safe Rule", Safe ]
        let updated = assertOk (applyOverrides overrides safeRule.Rule) "same level accepted"
        assertEqual Safe updated.Risk "unchanged")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestRiskEngine.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestRiskEngine.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/RiskEngine.fsx`.

- [ ] **Step 4: Write RiskEngine.fsx**

Create `src/RiskEngine.fsx`:

```fsharp
(**
    The safety core. Three independent gates decide whether anything may be
    removed. Each gate can only reject; none can approve on its own, and all
    three must pass.

      Gate 1  Rule assertion    — a matched rule must be Safe and SafeToDelete
      Gate 2  Allowlist anchor  — the path must be a strict descendant of the
                                  rule's resolved root, after normalization
      Gate 3  Denylist veto     — a hardcoded protected set, checked last

    The veto outranks --force and outranks configuration overrides. It has no
    bypass flag. Configuration overrides may only raise severity; an override
    that lowers it is a validation error rather than a silent clamp, because
    silently ignoring it would leave the user believing a restriction had been
    loosened when it had not.

    `DeletionCandidate` has a private constructor. The only way to obtain one
    is `evaluate`, so no caller anywhere in the codebase can hand the Cleaner
    an arbitrary path. "Delete /" is unreachable by construction rather than
    defended against at runtime.
*)
module RiskEngine

open System

open Types
open Utilities
open Rules

/// Why a path was refused. Every rejection is explicit and reportable.
type Rejection =
    /// The matched rule does not assert deletability.
    | NotSafeToDelete
    /// The rule's risk level exceeds what the current flags permit.
    | RiskTooHigh of RiskLevel
    /// The path is not a strict descendant of the rule's resolved root.
    | OutsideRuleRoot
    /// The path is inside a protected location. Names the protected ancestor.
    | Vetoed of string
    /// The path is a symlink; relocating it would move the link, not the target.
    | IsSymlink

module Rejection =
    /// A human-readable explanation for reports.
    let toText (r: Rejection) : string =
        match r with
        | NotSafeToDelete -> "the matching rule does not permit deletion"
        | RiskTooHigh level -> sprintf "risk level %s exceeds what the current flags allow" (RiskLevel.toText level)
        | OutsideRuleRoot -> "the path is not inside the rule's root directory"
        | Vetoed ancestor -> sprintf "the path is inside the protected location %s" ancestor
        | IsSymlink -> "the path is a symbolic link"

/// Paths that are never deletable, regardless of rules, flags, or config.
///
/// Returned as a function rather than a value because the home-relative
/// entries must resolve against the invoking user at call time.
let denylist () : string list =
    let home = Platform.home ()
    let underHome relative = IO.Path.Combine(home, relative)
    [ underHome "Documents"
      underHome "Desktop"
      underHome "Pictures"
      underHome "Movies"
      underHome "Music"
      underHome "Downloads"
      underHome "Library/Application Support"
      "/System"
      "/Library"
      "/Applications"
      "/usr"
      "/bin"
      "/sbin"
      "/etc"
      "/var/db" ]
    |> List.map Utilities.Path.normalize

/// Cache-safe subpaths that are exempt from the Application Support veto.
/// These are the only locations inside a protected directory that a rule may
/// legitimately target.
let private vetoExemptions () : string list =
    let home = Platform.home ()
    [ IO.Path.Combine(home, "Library/Application Support/CrashReporter") ]
    |> List.map Utilities.Path.normalize

/// True when a path is protected and must never be removed.
///
/// A path is vetoed when it is, or is inside, any denylist entry — unless it
/// is inside a narrower exemption. Additionally, any path inside a `.git`
/// directory is vetoed unconditionally: removing objects there can destroy
/// commits that exist nowhere else.
let isVetoed (path: string) : bool =
    let normalized = Utilities.Path.normalize path

    let inGitDirectory =
        Utilities.Path.segments normalized
        |> List.exists (fun segment -> segment = ".git")

    if inGitDirectory then true
    else
        let exempt =
            vetoExemptions ()
            |> List.exists (fun e ->
                Utilities.Path.equals e normalized || Utilities.Path.isDescendantOf e normalized)
        if exempt then false
        else
            denylist ()
            |> List.exists (fun protectedPath ->
                Utilities.Path.equals protectedPath normalized
                || Utilities.Path.isDescendantOf protectedPath normalized)

/// The protected ancestor responsible for a veto, for error reporting.
let private vetoingAncestor (path: string) : string =
    let normalized = Utilities.Path.normalize path
    if Utilities.Path.segments normalized |> List.exists (fun s -> s = ".git") then ".git"
    else
        denylist ()
        |> List.tryFind (fun p ->
            Utilities.Path.equals p normalized || Utilities.Path.isDescendantOf p normalized)
        |> Option.defaultValue "(protected)"

/// Applies a configuration risk override to a rule.
///
/// An override may only raise severity. Lowering is rejected with an
/// explanatory error rather than silently ignored. Raising risk also clears
/// `SafeToDelete`, so the two fields cannot drift out of agreement.
let applyOverrides (overrides: Map<string, RiskLevel>) (rule: Rule) : Result<Rule, string> =
    match overrides.TryFind rule.Name with
    | None -> Ok rule
    | Some requested ->
        let currentSeverity = RiskLevel.severity rule.Risk
        let requestedSeverity = RiskLevel.severity requested
        if requestedSeverity < currentSeverity then
            Error (
                sprintf
                    "riskOverrides cannot lower risk: rule '%s' is declared %s and the override requests %s. Overrides may only raise severity."
                    rule.Name (RiskLevel.toText rule.Risk) (RiskLevel.toText requested))
        elif requestedSeverity = currentSeverity then Ok rule
        else Ok { rule with Risk = requested; SafeToDelete = false }

/// A path that has passed all three gates and may be moved to Trash.
///
/// The constructor is private to this module. `evaluate` is the only way to
/// produce one, which is what makes an arbitrary path structurally incapable
/// of reaching the Cleaner.
type DeletionCandidate =
    private
        { Path: string
          RuleName: string
          Strategy: DeleteStrategy
          SizeBytes: int64 }

/// The path this candidate refers to.
let candidatePath (c: DeletionCandidate) : string = c.Path

/// The size in bytes of this candidate.
let candidateSize (c: DeletionCandidate) : int64 = c.SizeBytes

/// The name of the rule that produced this candidate.
let candidateRule (c: DeletionCandidate) : string = c.RuleName

/// The removal strategy declared by the producing rule.
let candidateStrategy (c: DeletionCandidate) : DeleteStrategy = c.Strategy

/// Runs all three gates against a file under a resolved rule.
///
/// `allowProbablySafe` corresponds to the --force flag. It widens gate 1 by
/// exactly one level and has no effect on gates 2 or 3.
let evaluate
    (allowProbablySafe: bool)
    (resolved: ResolvedRule)
    (file: FileMeta)
    : Result<DeletionCandidate, Rejection> =

    let rule = resolved.Rule

    // A symlink is refused before any gate: relocating it would move the link
    // rather than its target, which is never what the user asked for.
    if file.LinkTarget.IsSome then Error IsSymlink

    // Gate 1 — the rule must assert deletability at a permitted risk level.
    elif not rule.SafeToDelete then Error NotSafeToDelete
    else
        let permitted =
            match rule.Risk with
            | Safe -> true
            | ProbablySafe -> allowProbablySafe
            | Review | Dangerous | NeverDelete -> false

        if not permitted then Error (RiskTooHigh rule.Risk)

        // Gate 2 — the path must resolve to a strict descendant of the rule
        // root. isDescendantOf normalizes both operands first, so a traversal
        // escape is resolved before comparison rather than string-matched.
        elif not (Utilities.Path.isDescendantOf resolved.Root file.Path) then Error OutsideRuleRoot

        // Gate 3 — the veto, checked last so it overrides everything above.
        elif isVetoed file.Path then Error (Vetoed(vetoingAncestor file.Path))

        else
            Ok { Path = Utilities.Path.normalize file.Path
                 RuleName = rule.Name
                 Strategy = rule.Strategy
                 SizeBytes = file.SizeBytes }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 79 tests total.

Every one of the 21 RiskEngine tests must pass. **If any gate test fails, stop and fix it before proceeding to any later task.** A failing gate test means the tool can destroy data.

- [ ] **Step 6: Verify the private constructor actually blocks external construction**

This confirms the structural guarantee rather than trusting it. Create `/tmp/ctorprobe.fsx`:

```fsharp
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/RiskEngine.fsx"
open RiskEngine
// This must NOT compile. If it does, the structural guarantee is broken.
let forged = { Path = "/"; RuleName = "forged"; Strategy = Types.WholeDirectory; SizeBytes = 0L }
printfn "%A" forged
```

Run: `dotnet fsi /tmp/ctorprobe.fsx`

Expected: **a compile error**, specifically `FS1093: The union cases or fields of the type 'DeletionCandidate' are not accessible from this code location`.

If this file compiles and prints, the `private` modifier is not doing its job and must be fixed before continuing.

Clean up: `rm /tmp/ctorprobe.fsx`

- [ ] **Step 7: Verify the veto against the real home directory**

```bash
cat > /tmp/vetoprobe.fsx <<'EOF'
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/RiskEngine.fsx"
let home = Platform.home ()
let cases =
    [ IO.Path.Combine(home, "Documents/thesis.txt"), true
      IO.Path.Combine(home, "Desktop/notes.txt"), true
      IO.Path.Combine(home, "Library/Caches/com.apple.Safari/x"), false
      IO.Path.Combine(home, "Projects/app/.git/objects/ab/cd"), true
      "/System/Library/x", true
      "/tmp/scratch", false ]
let mutable failures = 0
for path, expected in cases do
    let actual = RiskEngine.isVetoed path
    if actual <> expected then
        failures <- failures + 1
        printfn "FAIL %s expected vetoed=%b got %b" path expected actual
    else printfn "ok   vetoed=%-5b %s" actual path
exit failures
EOF
dotnet fsi /tmp/vetoprobe.fsx; echo "exit=$?"; rm /tmp/vetoprobe.fsx
```

Expected: all six `ok` lines and `exit=0`.

- [ ] **Step 8: Commit**

```bash
git add src/RiskEngine.fsx tests/TestRiskEngine.fsx tests/Tests.fsx
git commit -m "feat: add three-gate risk engine

Gate 1 requires a Safe rule asserting deletability; gate 2 requires
strict descent from the rule root after normalization, which rejects
traversal escapes and prefix collisions; gate 3 is a denylist veto
that outranks --force and config overrides and has no bypass.

DeletionCandidate has a private constructor, so evaluate is the only
way to produce one and no caller can hand the Cleaner an arbitrary
path. Overrides may only raise severity; lowering is an error rather
than a silent clamp."
```

---

### Task 9: Report DTO layer

**Files:**
- Create: `src/Report/Dto.fsx`
- Create: `tests/TestDto.fsx`
- Modify: `tests/Tests.fsx`

**Why this task exists:** `System.Text.Json` **throws `NotSupportedException` on F# discriminated unions**. This was confirmed empirically before writing this plan. Every DU must be projected to a primitive before serialization. This task builds that projection once so no later report module has to think about it.

**Interfaces:**
- Consumes: `Types`, `Utilities`
- Produces:
  - `Dto.FindingDto` = `{ Path: string; RuleName: string; Category: string; Risk: string; Recoverability: string; Strategy: string; SizeBytes: int64; AgeDays: float; SafeToDelete: bool; Reason: string }`
  - `Dto.CategorySummaryDto` = `{ Category: string; ItemCount: int; TotalBytes: int64; ReclaimableBytes: int64 }`
  - `Dto.ScanResultDto` = `{ GeneratedAtUtc: string; Platform: string; ScanRoots: string list; TotalItems: int; TotalBytes: int64; ReclaimableBytes: int64; PendingReviewBytes: int64; Categories: CategorySummaryDto list; Findings: FindingDto list; Errors: string list; UnreadableDirectoryCount: int; ElapsedSeconds: float }`
  - `Dto.serializerOptions : JsonSerializerOptions`

- [ ] **Step 1: Write the failing test**

Create `tests/TestDto.fsx`:

```fsharp
open System.Text.Json
open TestFramework
open Dto

let run () =
    suite "Report.Dto"

    let sampleFinding =
        { Path = "/testroot/a.log"
          RuleName = "User Logs"
          Category = "Logs"
          Risk = "Safe"
          Recoverability = "Regenerated"
          Strategy = "ContentsOnly"
          SizeBytes = 4096L
          AgeDays = 12.5
          SafeToDelete = true
          Reason = "Diagnostic output." }

    let sampleResult =
        { GeneratedAtUtc = "2026-07-20T12:00:00Z"
          Platform = "MacOS"
          ScanRoots = [ "/testroot" ]
          TotalItems = 1
          TotalBytes = 4096L
          ReclaimableBytes = 4096L
          PendingReviewBytes = 0L
          Categories = [ { Category = "Logs"; ItemCount = 1; TotalBytes = 4096L; ReclaimableBytes = 4096L } ]
          Findings = [ sampleFinding ]
          Errors = []
          UnreadableDirectoryCount = 0
          ElapsedSeconds = 1.25 }

    test "a finding serializes without throwing" (fun () ->
        // System.Text.Json throws NotSupportedException on F# discriminated
        // unions. This test is the guard that keeps DUs out of the DTOs.
        let json = JsonSerializer.Serialize(sampleFinding, serializerOptions)
        assertContains "\"path\"" json "camelCase property naming"
        assertContains "User Logs" json "rule name present")

    test "a full scan result round-trips" (fun () ->
        let json = JsonSerializer.Serialize(sampleResult, serializerOptions)
        let restored = JsonSerializer.Deserialize<ScanResultDto>(json, serializerOptions)
        assertEqual sampleResult.TotalBytes restored.TotalBytes "total bytes survive"
        assertEqual 1 restored.Findings.Length "finding survives"
        assertEqual "User Logs" (List.head restored.Findings).RuleName "rule name survives")

    test "an empty scan result serializes" (fun () ->
        let empty = { sampleResult with Findings = []; Categories = []; TotalItems = 0 }
        let json = JsonSerializer.Serialize(empty, serializerOptions)
        assertContains "\"findings\":[]" (json.Replace(" ", "")) "empty array, not null")

    test "output is indented for human readability" (fun () ->
        let json = JsonSerializer.Serialize(sampleResult, serializerOptions)
        assertContains "\n" json "indented output contains newlines")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestDto.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestDto.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/Report/Dto.fsx`.

- [ ] **Step 4: Write Dto.fsx**

Create `src/Report/Dto.fsx`:

```fsharp
(**
    Serialization-safe projections of the domain types.

    System.Text.Json throws NotSupportedException on F# discriminated unions.
    Rather than register custom converters for each one, every DU is projected
    to a primitive string here, at a single boundary. The rule for this module
    is absolute: **no field of any DTO may be a discriminated union.**

    These records are also the JSON report's public contract, so their field
    names are part of the tool's interface and should change only deliberately.
*)
module Dto

open System
open System.Text.Json
open System.Text.Json.Serialization

/// One reported item.
type FindingDto =
    { Path: string
      RuleName: string
      Category: string
      Risk: string
      Recoverability: string
      Strategy: string
      SizeBytes: int64
      AgeDays: float
      SafeToDelete: bool
      Reason: string }

/// Aggregate figures for one category.
type CategorySummaryDto =
    { Category: string
      ItemCount: int
      TotalBytes: int64
      /// Bytes that passed all three gates and could be moved to Trash.
      ReclaimableBytes: int64 }

/// A complete scan, as written to the JSON report.
type ScanResultDto =
    { GeneratedAtUtc: string
      Platform: string
      ScanRoots: string list
      TotalItems: int
      TotalBytes: int64
      /// Bytes eligible for automatic removal.
      ReclaimableBytes: int64
      /// Bytes surfaced for human judgment. Deliberately kept distinct from
      /// ReclaimableBytes: conflating them would let a user believe space had
      /// been freed when it had only been listed.
      PendingReviewBytes: int64
      Categories: CategorySummaryDto list
      Findings: FindingDto list
      Errors: string list
      UnreadableDirectoryCount: int
      ElapsedSeconds: float }

/// Shared serializer settings: camelCase names, indented output, and no
/// converters for DUs — because no DTO here contains one.
let serializerOptions =
    let options =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never)
    options
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 83 tests total.

If `test "a finding serializes without throwing"` reports `NotSupportedException`, a DU has crept into a DTO field. Find it and replace it with its `toText` projection.

- [ ] **Step 6: Commit**

```bash
git add src/Report/Dto.fsx tests/TestDto.fsx tests/Tests.fsx
git commit -m "feat: add serialization-safe DTO layer

System.Text.Json throws NotSupportedException on F# discriminated
unions, so every DU is projected to a string at this single boundary.
No DTO field may be a DU; the serialization test is the guard."
```

---

### Task 10: FileClassifier

**Files:**
- Create: `src/FileClassifier.fsx`
- Create: `tests/TestFileClassifier.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`
- Produces:
  - `FileClassifier.classify : string -> Category`
  - `FileClassifier.isArchive : string -> bool`
  - `FileClassifier.isDiskImage : string -> bool`

- [ ] **Step 1: Write the failing tests**

Create `tests/TestFileClassifier.fsx`:

```fsharp
open TestFramework
open Types
open FileClassifier

let run () =
    suite "FileClassifier"

    test "archives are recognized by extension" (fun () ->
        for name in [ "a.zip"; "a.tar"; "a.tar.gz"; "a.tgz"; "a.rar"; "a.7z"; "a.bz2" ] do
            assertEqual Archives (classify ("/x/" + name)) (sprintf "%s is an archive" name))

    test "disk images are recognized by extension" (fun () ->
        for name in [ "a.dmg"; "a.iso"; "a.img"; "a.vmdk"; "a.qcow2" ] do
            assertEqual DiskImages (classify ("/x/" + name)) (sprintf "%s is a disk image" name))

    test "installers are recognized by extension" (fun () ->
        for name in [ "a.pkg"; "a.msi"; "a.deb"; "a.rpm" ] do
            assertEqual Installers (classify ("/x/" + name)) (sprintf "%s is an installer" name))

    test "logs are recognized by extension and by rotation suffix" (fun () ->
        assertEqual Logs (classify "/x/app.log") "plain log"
        assertEqual Logs (classify "/x/app.log.1") "rotated log"
        assertEqual Logs (classify "/x/app.log.gz") "compressed rotated log")

    test "crash reports are recognized" (fun () ->
        assertEqual CrashReports (classify "/x/App_2024-01-01.crash") "crash extension"
        assertEqual CrashReports (classify "/x/App.ips") "ips extension")

    test "temporary files are recognized" (fun () ->
        assertEqual TemporaryFiles (classify "/x/a.tmp") "tmp extension"
        assertEqual TemporaryFiles (classify "/x/a.temp") "temp extension"
        assertEqual TemporaryFiles (classify "/x/~a.swp") "swap file")

    test "path context overrides extension when a directory is decisive" (fun () ->
        // A .log inside a cache directory is still cache, because the
        // enclosing directory is the stronger signal about regenerability.
        assertEqual Caches (classify "/Users/x/Library/Caches/app/debug.log") "cache directory wins"
        assertEqual DeveloperArtifacts (classify "/Users/x/Library/Developer/Xcode/DerivedData/a/b.o") "DerivedData wins")

    test "node_modules and __pycache__ are developer artifacts" (fun () ->
        assertEqual DeveloperArtifacts (classify "/p/node_modules/left-pad/index.js") "node_modules"
        assertEqual DeveloperArtifacts (classify "/p/__pycache__/mod.cpython-312.pyc") "__pycache__")

    test "an unrecognized file falls through to Other" (fun () ->
        assertEqual Other (classify "/x/notes.xyz") "unknown extension")

    test "classification is case-insensitive" (fun () ->
        assertEqual Archives (classify "/x/BACKUP.ZIP") "uppercase extension")

    test "isArchive and isDiskImage agree with classify" (fun () ->
        assertTrue (isArchive "/x/a.zip") "zip is an archive"
        assertFalse (isArchive "/x/a.dmg") "dmg is not an archive"
        assertTrue (isDiskImage "/x/a.dmg") "dmg is a disk image")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestFileClassifier.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestFileClassifier.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/FileClassifier.fsx`.

- [ ] **Step 4: Write FileClassifier.fsx**

Create `src/FileClassifier.fsx`:

```fsharp
(**
    Classifies a path into a report category.

    Directory context is checked before file extension, because the enclosing
    directory is the stronger signal about regenerability: a `.log` inside
    ~/Library/Caches is cache that will be regenerated, not a log worth keeping.

    Classification is presentation only. It groups findings in reports and has
    no bearing on whether anything may be deleted — that is entirely the risk
    engine's decision.
*)
module FileClassifier

open System

open Types
open Utilities

/// Directory names that determine a category regardless of file extension.
/// Ordered most specific first.
let private decisiveDirectories =
    [ "DerivedData", DeveloperArtifacts
      "node_modules", DeveloperArtifacts
      "__pycache__", DeveloperArtifacts
      "CoreSimulator", DeveloperArtifacts
      "CrashReporter", CrashReports
      "DiagnosticReports", CrashReports
      "Caches", Caches
      "_cacache", PackageManagerCache
      "Logs", Logs ]

/// Extension groups. Compound extensions such as `.tar.gz` are handled by
/// suffix matching rather than by Path.GetExtension, which returns only `.gz`.
let private extensionGroups =
    [ [ ".zip"; ".tar"; ".tar.gz"; ".tgz"; ".tar.bz2"; ".rar"; ".7z"; ".bz2"; ".xz" ], Archives
      [ ".dmg"; ".iso"; ".img"; ".vmdk"; ".qcow2"; ".vdi"; ".sparseimage" ], DiskImages
      [ ".pkg"; ".msi"; ".deb"; ".rpm"; ".exe" ], Installers
      [ ".crash"; ".ips"; ".diag"; ".panic" ], CrashReports
      [ ".tmp"; ".temp"; ".swp"; ".swo"; ".partial"; ".download" ], TemporaryFiles
      [ ".log"; ".log.gz" ], Logs
      [ ".o"; ".obj"; ".pyc"; ".class"; ".dSYM" ], DeveloperArtifacts ]

/// True when the lowercase filename ends with any extension in the list.
let private hasExtension (extensions: string list) (lowerName: string) : bool =
    extensions |> List.exists (fun e -> lowerName.EndsWith(e, StringComparison.Ordinal))

/// True when a filename is a rotated log such as `app.log.1`.
let private isRotatedLog (lowerName: string) : bool =
    let parts = lowerName.Split '.'
    parts.Length >= 3
    && parts.[parts.Length - 2] = "log"
    && parts.[parts.Length - 1] |> Seq.forall Char.IsDigit

/// Assigns a report category to a path.
let classify (path: string) : Category =
    let normalized = Utilities.Path.normalize path
    let segments = Utilities.Path.segments normalized
    let lowerName =
        match List.tryLast segments with
        | Some name -> name.ToLowerInvariant()
        | None -> ""

    // Directory context first — it is the stronger signal.
    let fromDirectory =
        decisiveDirectories
        |> List.tryPick (fun (directoryName, category) ->
            if segments |> List.exists (fun s -> String.Equals(s, directoryName, StringComparison.OrdinalIgnoreCase))
            then Some category
            else None)

    match fromDirectory with
    | Some category -> category
    | None ->
        if isRotatedLog lowerName then Logs
        else
            extensionGroups
            |> List.tryPick (fun (extensions, category) ->
                if hasExtension extensions lowerName then Some category else None)
            |> Option.defaultValue Other

/// True when the path names a compressed archive.
let isArchive (path: string) : bool = classify path = Archives

/// True when the path names a disk or virtual-machine image.
let isDiskImage (path: string) : bool = classify path = DiskImages
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 94 tests total.

- [ ] **Step 6: Commit**

```bash
git add src/FileClassifier.fsx tests/TestFileClassifier.fsx tests/Tests.fsx
git commit -m "feat: add file classifier for report grouping

Directory context is checked before extension, since the enclosing
directory is the stronger signal about regenerability. Classification
is presentation only and has no bearing on deletability."
```

---

### Task 11: DirectoryWalker

**Files:**
- Create: `src/DirectoryWalker.fsx`
- Create: `tests/TestDirectoryWalker.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`, `Logging`, `Diagnostics`, `Platform`
- Produces:
  - `DirectoryWalker.WalkOptions` = `{ MaxDepth: int; ThreadCount: int; IgnorePaths: string list; FollowSymlinks: bool; StayOnVolume: bool }`
  - `DirectoryWalker.defaultOptions : WalkOptions`
  - `DirectoryWalker.WalkOutcome` = `{ Files: FileMeta list; Errors: FsError list }`
  - `DirectoryWalker.walk : FileSystemOps -> Counters -> WalkOptions -> string -> WalkOutcome`

**Design note:** the walker is written with a simple explicit stack rather than `Parallel.ForEachAsync` for the first implementation. Parallelism is added in Task 17 only if profiling on a real home directory shows it is worth the complexity. A correct sequential walker that ships beats a parallel one that races.

- [ ] **Step 1: Write the failing tests**

Create `tests/TestDirectoryWalker.fsx`:

```fsharp
open TestFramework
open Types
open Platform
open DirectoryWalker

let private tree =
    [ { Path = "/root"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/root/a.log"; SizeBytes = 100L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 }
      { Path = "/root/sub"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/root/sub/b.log"; SizeBytes = 200L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 }
      { Path = "/root/sub/deep"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/root/sub/deep/c.log"; SizeBytes = 300L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 }
      { Path = "/root/ignored"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/root/ignored/d.log"; SizeBytes = 400L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 }
      { Path = "/root/link"; SizeBytes = 0L; IsDirectory = true; LinkTarget = Some "/elsewhere"; ModifiedDaysAgo = 1.0 } ]

let run () =
    suite "DirectoryWalker"

    let fs = FileSystem.inMemory tree

    test "walks the full tree by default" (fun () ->
        let counters = Diagnostics.create ()
        let outcome = walk fs counters defaultOptions "/root"
        let paths = outcome.Files |> List.map (fun f -> f.Path) |> List.sort
        assertEqual 4 paths.Length "a.log, b.log, c.log, d.log"
        assertTrue (paths |> List.contains "/root/sub/deep/c.log") "reaches the deepest file")

    test "does not descend into symlinked directories" (fun () ->
        // Following links risks infinite recursion and escaping the scan root.
        let counters = Diagnostics.create ()
        let outcome = walk fs counters defaultOptions "/root"
        assertFalse (outcome.Files |> List.exists (fun f -> f.Path.Contains "elsewhere")) "link not followed"
        assertEqual 1L (Diagnostics.snapshot counters).SkipsByReason.[Diagnostics.Symlink] "symlink skip recorded")

    test "respects the depth limit" (fun () ->
        let counters = Diagnostics.create ()
        let outcome = walk fs counters { defaultOptions with MaxDepth = 1 } "/root"
        let paths = outcome.Files |> List.map (fun f -> f.Path)
        assertTrue (paths |> List.contains "/root/a.log") "depth 1 file included"
        assertFalse (paths |> List.contains "/root/sub/deep/c.log") "depth 3 file excluded")

    test "skips ignored paths" (fun () ->
        let counters = Diagnostics.create ()
        let options = { defaultOptions with IgnorePaths = [ "/root/ignored" ] }
        let outcome = walk fs counters options "/root"
        assertFalse (outcome.Files |> List.exists (fun f -> f.Path.Contains "ignored")) "ignored subtree excluded"
        assertEqual 3 outcome.Files.Length "three remaining files")

    test "counters reflect what was examined" (fun () ->
        let counters = Diagnostics.create ()
        walk fs counters defaultOptions "/root" |> ignore
        let s = Diagnostics.snapshot counters
        assertEqual 4L s.Files "four files counted"
        assertEqual 1000L s.Bytes "summed sizes")

    test "a missing root yields an error rather than throwing" (fun () ->
        let counters = Diagnostics.create ()
        let outcome = walk fs counters defaultOptions "/nonexistent"
        assertEqual 0 outcome.Files.Length "no files"
        assertEqual 1 outcome.Errors.Length "one error recorded"
        assertEqual (PathNotFound "/nonexistent") (List.head outcome.Errors) "PathNotFound")

    test "traversal continues past an unreadable subdirectory" (fun () ->
        // Permission denial is the common case on macOS, not an edge case.
        // A denial in one subtree must not abort the whole scan.
        let denying =
            { fs with
                EnumerateFiles = fun path ->
                    if path.Contains "sub" then Error (PermissionDenied path)
                    else fs.EnumerateFiles path }
        let counters = Diagnostics.create ()
        let outcome = walk denying counters defaultOptions "/root"
        assertTrue (outcome.Files |> List.exists (fun f -> f.Path = "/root/a.log")) "readable files still returned"
        assertTrue (outcome.Errors |> List.exists (function PermissionDenied _ -> true | _ -> false)) "denial recorded")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestDirectoryWalker.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestDirectoryWalker.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/DirectoryWalker.fsx`.

- [ ] **Step 4: Write DirectoryWalker.fsx**

Create `src/DirectoryWalker.fsx`:

```fsharp
(**
    Filesystem traversal with error collection.

    Two properties matter more than speed here.

    First, **errors are collected, never thrown**. On macOS, permission denial
    is the common case: TCC blocks ~/Library/Containers, ~/Library/Mail, and
    others unless the terminal has Full Disk Access. A denial in one subtree
    must not abort the scan; it is recorded and traversal continues.

    Second, **symlinks are never followed**. Following them risks unbounded
    recursion through a cycle and, worse, escaping the scan root entirely —
    which would let the risk engine evaluate paths the user never asked about.

    Traversal uses an explicit stack rather than recursion, so a pathologically
    deep tree cannot overflow the call stack.
*)
module DirectoryWalker

open System
open System.Collections.Generic

open Types
open Utilities

/// Traversal settings.
type WalkOptions =
    { /// Maximum directory depth below the root. Bounds pathological trees.
      MaxDepth: int
      /// Reserved for the parallel implementation added in Task 17.
      ThreadCount: int
      /// Absolute paths whose subtrees are skipped entirely.
      IgnorePaths: string list
      /// Always false. Present to make the decision explicit and greppable
      /// rather than implicit in the traversal code.
      FollowSymlinks: bool
      /// Whether to stop at a filesystem boundary, so a mounted volume or
      /// network share is not pulled into a scan of the local disk.
      StayOnVolume: bool }

/// Sensible defaults: deep enough for real trees, bounded against pathological ones.
let defaultOptions: WalkOptions =
    { MaxDepth = 32
      ThreadCount = min Environment.ProcessorCount 8
      IgnorePaths = []
      FollowSymlinks = false
      StayOnVolume = true }

/// What a traversal produced.
type WalkOutcome =
    { Files: FileMeta list
      Errors: FsError list }

/// Walks a directory tree, returning every file found and every error hit.
///
/// Never throws. Every failure is recorded in `Errors` and traversal continues
/// with the next entry.
let walk
    (fs: Platform.FileSystemOps)
    (counters: Diagnostics.Counters)
    (options: WalkOptions)
    (root: string)
    : WalkOutcome =

    let files = ResizeArray<FileMeta>()
    let errors = ResizeArray<FsError>()

    let ignored =
        options.IgnorePaths |> List.map Utilities.Path.normalize

    /// True when a path lies inside a configured ignore root.
    let isIgnored (path: string) =
        let normalized = Utilities.Path.normalize path
        ignored |> List.exists (fun i ->
            Utilities.Path.equals i normalized || Utilities.Path.isDescendantOf i normalized)

    let rootVolume =
        if options.StayOnVolume then
            match fs.VolumeOf root with
            | Ok volume -> Some volume
            | Error _ -> None
        else None

    /// True when a directory sits on a different filesystem than the root.
    let crossesVolume (path: string) =
        match rootVolume with
        | None -> false
        | Some expected ->
            match fs.VolumeOf path with
            | Ok actual -> actual <> expected
            | Error _ -> false

    if not (fs.Exists root) then
        { Files = []; Errors = [ PathNotFound root ] }
    else
        // Explicit stack of (directory, depth) so deep trees cannot overflow.
        let pending = Stack<string * int>()
        pending.Push(root, 0)

        while pending.Count > 0 do
            let directory, depth = pending.Pop()
            Diagnostics.recordDirectory counters

            // Files in this directory.
            match fs.EnumerateFiles directory with
            | Error e -> errors.Add e
            | Ok found ->
                for path in found do
                    if isIgnored path then
                        Diagnostics.recordSkip counters Diagnostics.Excluded
                    else
                        match fs.GetInfo path with
                        | Error e -> errors.Add e
                        | Ok meta ->
                            if FileMeta.isLink meta then
                                Diagnostics.recordSkip counters Diagnostics.Symlink
                            else
                                files.Add meta
                                Diagnostics.recordFile counters meta.SizeBytes

            // Subdirectories, queued for a later iteration.
            if depth >= options.MaxDepth then
                Diagnostics.recordSkip counters Diagnostics.DepthLimit
            else
                match fs.EnumerateDirectories directory with
                | Error e -> errors.Add e
                | Ok found ->
                    for path in found do
                        if isIgnored path then
                            Diagnostics.recordSkip counters Diagnostics.Excluded
                        else
                            match fs.GetInfo path with
                            | Error e -> errors.Add e
                            | Ok meta ->
                                if FileMeta.isLink meta then
                                    Diagnostics.recordSkip counters Diagnostics.Symlink
                                elif crossesVolume path then
                                    Diagnostics.recordSkip counters Diagnostics.FilesystemBoundary
                                else
                                    pending.Push(path, depth + 1)

        { Files = List.ofSeq files; Errors = List.ofSeq errors }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 101 tests total.

- [ ] **Step 6: Walk a real directory to confirm behavior on disk**

```bash
cat > /tmp/walkprobe.fsx <<'EOF'
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/RiskEngine.fsx"
#load "src/FileClassifier.fsx"
#load "src/DirectoryWalker.fsx"
let fs = Platform.FileSystem.real
let counters = Diagnostics.create ()
let target = System.IO.Path.Combine(Platform.home (), "Library/Caches")
let outcome = DirectoryWalker.walk fs counters { DirectoryWalker.defaultOptions with MaxDepth = 3 } target
printfn "files=%d errors=%d" outcome.Files.Length outcome.Errors.Length
printfn "%s" (Diagnostics.render (Diagnostics.snapshot counters))
EOF
dotnet fsi /tmp/walkprobe.fsx && rm /tmp/walkprobe.fsx
```

Expected: a non-zero file count and a diagnostics block. A non-zero error count is normal and expected — it reflects TCC-protected subdirectories. The important property is that the walk **completes** despite them.

- [ ] **Step 7: Commit**

```bash
git add src/DirectoryWalker.fsx tests/TestDirectoryWalker.fsx tests/Tests.fsx
git commit -m "feat: add directory walker with error collection

Errors are collected rather than thrown, because permission denial is
the common case on macOS under TCC and must not abort a scan.
Symlinks are never followed — that would risk cycles and escaping the
scan root. Traversal uses an explicit stack so deep trees cannot
overflow."
```

---

### Task 12: Scanner

**Files:**
- Create: `src/Scanner.fsx`
- Create: `tests/TestScanner.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`, `Platform`, `Rules`, `RiskEngine`, `FileClassifier`, `DirectoryWalker`, `Configuration`, `Diagnostics`
- Produces:
  - `Scanner.Finding` = `{ Meta: FileMeta; RuleName: string; Category: Category; Risk: RiskLevel; Recoverability: Recoverability; Strategy: DeleteStrategy; Reason: string; Candidate: RiskEngine.DeletionCandidate option }`
  - `Scanner.ScanResult` = `{ GeneratedAtUtc: DateTime; Platform: Types.Platform; ScanRoots: string list; Findings: Finding list; Errors: FsError list; Elapsed: TimeSpan; Counters: Diagnostics.Snapshot }`
  - `Scanner.scan : FileSystemOps -> Config -> allowProbablySafe: bool -> ResolvedRule list -> Result<ScanResult, string list>`
  - `Scanner.reclaimableBytes : ScanResult -> int64`
  - `Scanner.pendingReviewBytes : ScanResult -> int64`
  - `Scanner.byCategory : ScanResult -> (Category * Finding list) list`

**Key behavior:** a file is matched against rules **in precedence order**, and the **first** match wins. This is what makes the Containers requirement work — the narrow cache rule is checked before the broad catch-all.

- [ ] **Step 1: Write the failing tests**

Create `tests/TestScanner.fsx`:

```fsharp
open System
open TestFramework
open Types
open Platform
open Scanner

/// A minimal rule builder for tests.
let private testRule name root risk safeToDelete includes =
    { Rules.Name = name
      Rules.Platforms = Set.ofList [ MacOS; Linux; Windows ]
      Rules.Root = Absolute root
      Rules.Include = includes
      Rules.Exclude = []
      Rules.Risk = risk
      Rules.Recoverability = Regenerated
      Rules.Strategy = ContentsOnly
      Rules.MinAge = None
      Rules.MinSize = None
      Rules.SafeToDelete = safeToDelete
      Rules.Description = "test rule"
      Rules.Reason = "test reason"
      Rules.EstimatedBenefit = "test"
      Rules.Examples = [ root ]
      Rules.Category = Caches }

let private tree =
    [ { Path = "/cache"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/cache/a.tmp"; SizeBytes = 1000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 }
      { Path = "/cache/b.tmp"; SizeBytes = 2000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 60.0 }
      { Path = "/data"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/data/keep.txt"; SizeBytes = 3000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 } ]

let run () =
    suite "Scanner"

    let fs = FileSystem.inMemory tree
    let config = Configuration.defaults

    test "a Safe rule produces findings with deletion candidates" (fun () ->
        let rules = [ { Rules.Rule = testRule "Cache" "/cache" Safe true []; Rules.Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 2 result.Findings.Length "two cache files found"
        assertTrue (result.Findings |> List.forall (fun f -> f.Candidate.IsSome)) "all have candidates")

    test "a Review rule produces findings with no candidates" (fun () ->
        let rules = [ { Rules.Rule = testRule "Data" "/data" Review false []; Rules.Root = "/data" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "one file found"
        assertEqual None (List.head result.Findings).Candidate "no candidate for a Review rule")

    test "reclaimable and pending-review bytes are reported separately" (fun () ->
        // Conflating these would let a user believe space was freed when it
        // had only been listed for review.
        let rules =
            [ { Rules.Rule = testRule "Cache" "/cache" Safe true []; Rules.Root = "/cache" }
              { Rules.Rule = testRule "Data" "/data" Review false []; Rules.Root = "/data" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 3000L (reclaimableBytes result) "cache bytes are reclaimable"
        assertEqual 3000L (pendingReviewBytes result) "data bytes are pending review")

    test "the first rule in precedence order wins" (fun () ->
        // This is the Containers requirement: a narrow Safe rule must beat a
        // broad Review catch-all covering the same path.
        let narrow = { Rules.Rule = testRule "Narrow" "/cache" Safe true []; Rules.Root = "/cache" }
        let broad = { Rules.Rule = testRule "Broad" "/" Review false []; Rules.Root = "/" }
        let ordered = Rules.byPrecedence [ broad; narrow ]
        let result = assertOk (scan fs config false ordered) "scan succeeds"
        let cacheFinding = result.Findings |> List.find (fun f -> f.Meta.Path = "/cache/a.tmp")
        assertEqual "Narrow" cacheFinding.RuleName "the more specific rule matched")

    test "MinAge excludes items younger than the threshold" (fun () ->
        let aged = { testRule "Cache" "/cache" Safe true [] with Rules.MinAge = Some (TimeSpan.FromDays 30.0) }
        let rules = [ { Rules.Rule = aged; Rules.Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "only the 60-day-old file qualifies"
        assertEqual "/cache/b.tmp" (List.head result.Findings).Meta.Path "the older file")

    test "MinSize excludes items below the threshold" (fun () ->
        let sized = { testRule "Cache" "/cache" Safe true [] with Rules.MinSize = Some 1500L }
        let rules = [ { Rules.Rule = sized; Rules.Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "only the 2000-byte file qualifies")

    test "include globs restrict what a rule matches" (fun () ->
        let rules = [ { Rules.Rule = testRule "Cache" "/cache" Safe true [ "*.nomatch" ]; Rules.Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 0 result.Findings.Length "no file matches the include glob")

    test "ignored extensions from config are excluded" (fun () ->
        let rules = [ { Rules.Rule = testRule "Cache" "/cache" Safe true []; Rules.Root = "/cache" } ]
        let withIgnore = { config with IgnoreExtensions = [ ".tmp" ] }
        let result = assertOk (scan fs withIgnore false rules) "scan succeeds"
        assertEqual 0 result.Findings.Length "tmp files excluded by config")

    test "a lowering risk override aborts the scan with an error" (fun () ->
        let rules = [ { Rules.Rule = testRule "Data" "/data" Review false []; Rules.Root = "/data" } ]
        let bad = { config with RiskOverrides = Map.ofList [ "Data", Safe ] }
        let errors = assertError (scan fs bad false rules) "lowering override rejected"
        assertContains "lower" ((List.head errors).ToLowerInvariant()) "explains the direction")

    test "byCategory groups findings and drops empty categories" (fun () ->
        let rules = [ { Rules.Rule = testRule "Cache" "/cache" Safe true []; Rules.Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        let grouped = byCategory result
        assertEqual 1 grouped.Length "one non-empty category"
        assertEqual 2 (snd (List.head grouped)).Length "two findings in it")

    test "walk errors are carried into the result" (fun () ->
        let denying = { fs with EnumerateFiles = fun p -> Error (PermissionDenied p) }
        let rules = [ { Rules.Rule = testRule "Cache" "/cache" Safe true []; Rules.Root = "/cache" } ]
        let result = assertOk (scan denying config false rules) "scan still succeeds"
        assertTrue (result.Errors.Length > 0) "errors reported"
        assertEqual 0 result.Findings.Length "no findings from an unreadable tree")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestScanner.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestScanner.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/Scanner.fsx`.

- [ ] **Step 4: Write Scanner.fsx**

Create `src/Scanner.fsx`:

```fsharp
(**
    Assembles a scan: walk each rule root, match files against rules in
    precedence order, and ask the risk engine whether each match may be removed.

    The scanner contains **no rule-specific logic**. It is parameterized over a
    resolved rule list, so adding a rule to the catalog changes nothing here.

    First match wins. Because rules arrive sorted most-specific-first, a narrow
    rule inside a broad one takes precedence — which is exactly what makes the
    ~/Library/Containers requirement work: the cache rule is checked before the
    catch-all, so only Data/Library/Caches and Data/tmp come back Safe.
*)
module Scanner

open System

open Types
open Utilities

/// One reported item, with its deletion candidate when it passed all gates.
type Finding =
    { Meta: FileMeta
      RuleName: string
      Category: Category
      Risk: RiskLevel
      Recoverability: Recoverability
      Strategy: DeleteStrategy
      Reason: string
      /// Present only when all three risk gates passed. `None` means the item
      /// is reported but may not be removed.
      Candidate: RiskEngine.DeletionCandidate option }

/// A complete scan.
type ScanResult =
    { GeneratedAtUtc: DateTime
      Platform: Types.Platform
      ScanRoots: string list
      Findings: Finding list
      Errors: FsError list
      Elapsed: TimeSpan
      Counters: Diagnostics.Snapshot }

/// True when a file satisfies a rule's include globs, exclude globs, age
/// floor, and size floor.
let private satisfies (now: DateTime) (root: string) (rule: Rules.Rule) (meta: FileMeta) : bool =
    // Globs are matched against the path relative to the rule root, so a rule
    // does not have to know where it was resolved to.
    let relative =
        let normalizedRoot = Utilities.Path.normalize root
        let normalizedPath = Utilities.Path.normalize meta.Path
        if normalizedPath.Length > normalizedRoot.Length + 1
        then normalizedPath.Substring(normalizedRoot.Length + 1)
        else normalizedPath

    let includeOk =
        List.isEmpty rule.Include || Utilities.Glob.matchesAny rule.Include relative

    let excludeOk =
        List.isEmpty rule.Exclude || not (Utilities.Glob.matchesAny rule.Exclude relative)

    let ageOk =
        match rule.MinAge with
        | None -> true
        | Some minimum -> FileMeta.age now meta >= minimum

    let sizeOk =
        match rule.MinSize with
        | None -> true
        | Some minimum -> meta.SizeBytes >= minimum

    includeOk && excludeOk && ageOk && sizeOk

/// Finds the first rule in precedence order that claims this file.
let private firstMatch
    (now: DateTime)
    (rules: Rules.ResolvedRule list)
    (meta: FileMeta)
    : Rules.ResolvedRule option =
    rules
    |> List.tryFind (fun resolved ->
        Utilities.Path.isDescendantOf resolved.Root meta.Path
        && satisfies now resolved.Root resolved.Rule meta)

/// Runs a scan over every resolved rule root.
///
/// `allowProbablySafe` corresponds to --force and widens only the risk gate.
/// Returns Error when configuration overrides are invalid, because continuing
/// would apply a risk policy the user did not actually get.
let scan
    (fs: Platform.FileSystemOps)
    (config: Configuration.Config)
    (allowProbablySafe: bool)
    (rules: Rules.ResolvedRule list)
    : Result<ScanResult, string list> =

    // Apply configuration overrides first. A lowering override is fatal.
    let overrideResults =
        rules
        |> List.map (fun resolved ->
            RiskEngine.applyOverrides config.RiskOverrides resolved.Rule
            |> Result.map (fun updated -> { resolved with Rules.Rule = updated }))

    let overrideErrors =
        overrideResults |> List.choose (function Error e -> Some e | Ok _ -> None)

    if not (List.isEmpty overrideErrors) then Error overrideErrors
    else

    let effectiveRules =
        overrideResults |> List.choose (function Ok r -> Some r | Error _ -> None)

    let now = DateTime.UtcNow
    let counters = Diagnostics.create ()
    let walkOptions =
        { DirectoryWalker.defaultOptions with
            DirectoryWalker.MaxDepth = 32
            DirectoryWalker.ThreadCount = config.ThreadCount
            DirectoryWalker.IgnorePaths = config.IgnorePaths }

    // Distinct roots, so two rules sharing a root do not walk it twice.
    let roots =
        effectiveRules
        |> List.map (fun r -> r.Root)
        |> List.distinct

    let outcomes = roots |> List.map (DirectoryWalker.walk fs counters walkOptions)

    let allFiles = outcomes |> List.collect (fun o -> o.Files)
    let allErrors = outcomes |> List.collect (fun o -> o.Errors)

    let ignoredExtension (path: string) =
        let lower = path.ToLowerInvariant()
        config.IgnoreExtensions |> List.exists (fun e -> lower.EndsWith(e, StringComparison.Ordinal))

    let findings =
        allFiles
        |> List.filter (fun meta -> not (ignoredExtension meta.Path))
        |> List.choose (fun meta ->
            match firstMatch now effectiveRules meta with
            | None -> None
            | Some resolved ->
                let rule = resolved.Rule
                // The risk engine is the sole authority on deletability. A
                // rejection here is normal, not an error: most findings are
                // reported without being removable.
                let candidate =
                    match RiskEngine.evaluate allowProbablySafe resolved meta with
                    | Ok c -> Some c
                    | Error _ -> None
                Some
                    { Meta = meta
                      RuleName = rule.Name
                      Category = rule.Category
                      Risk = rule.Risk
                      Recoverability = rule.Recoverability
                      Strategy = rule.Strategy
                      Reason = rule.Reason
                      Candidate = candidate })

    Ok
        { GeneratedAtUtc = now
          Platform = Platform.current ()
          ScanRoots = roots
          Findings = findings
          Errors = allErrors
          Elapsed = counters.Clock.Elapsed
          Counters = Diagnostics.snapshot counters }

/// Total bytes that passed every gate and could be moved to Trash.
let reclaimableBytes (result: ScanResult) : int64 =
    result.Findings
    |> List.filter (fun f -> f.Candidate.IsSome)
    |> List.sumBy (fun f -> f.Meta.SizeBytes)

/// Total bytes reported for human judgment but not automatically removable.
/// Deliberately distinct from `reclaimableBytes` — see the report modules,
/// which must never present the two as one number.
let pendingReviewBytes (result: ScanResult) : int64 =
    result.Findings
    |> List.filter (fun f -> f.Candidate.IsNone)
    |> List.sumBy (fun f -> f.Meta.SizeBytes)

/// Groups findings by category, largest total first, dropping empty categories.
let byCategory (result: ScanResult) : (Category * Finding list) list =
    result.Findings
    |> List.groupBy (fun f -> f.Category)
    |> List.sortByDescending (fun (_, findings) -> findings |> List.sumBy (fun f -> f.Meta.SizeBytes))

/// The count of directories that could not be read, for the Full Disk Access hint.
let unreadableDirectoryCount (result: ScanResult) : int =
    result.Errors
    |> List.filter (function PermissionDenied _ -> true | _ -> false)
    |> List.length
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 112 tests total.

- [ ] **Step 6: Commit**

```bash
git add src/Scanner.fsx tests/TestScanner.fsx tests/Tests.fsx
git commit -m "feat: add scanner assembling walk, rules, and risk gates

First match wins over a precedence-sorted rule list, which is what
makes a narrow rule inside a broad one take effect. The scanner holds
no rule-specific logic. Reclaimable and pending-review bytes are
computed separately and must stay that way in reports."
```

---

### Task 13: Console report, JSON report, CLI, and entry point

**This task produces the first end-to-end usable version.** After it, `dotnet fsi DiskCleanupAdvisor.fsx` performs a real scan and prints a real report. Nothing can be deleted yet — `Cleaner` does not exist until Task 15.

**Files:**
- Create: `src/Report/Console.fsx`
- Create: `src/Report/Json.fsx`
- Create: `src/Cli.fsx`
- Create: `DiskCleanupAdvisor.fsx`
- Create: `tests/TestCli.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: everything through `Scanner`, plus `Dto`
- Produces:
  - `Cli.Options` = `{ Mode: Mode; JsonPath: string option; MarkdownPath: string option; HtmlPath: string option; ConfigPath: string option; Duplicates: bool; LargeFiles: bool; AgeDays: int option; MinSizeBytes: int64 option; Threads: int option; HashAlgorithm: string option; Force: bool; AllowRoot: bool; Verbose: bool }`
  - `Cli.Mode` = `Scan | Delete | Help`
  - `Cli.parse : string list -> Result<Options, string list>`
  - `Cli.helpText : string`
  - `Report.Console.render : ScanResult -> string`
  - `Report.Json.write : string -> ScanResult -> Result<unit, string>`
  - `Report.Json.toDto : ScanResult -> Dto.ScanResultDto`

- [ ] **Step 1: Write the failing CLI tests**

Create `tests/TestCli.fsx`:

```fsharp
open TestFramework
open Cli

let run () =
    suite "Cli.parse"

    test "no arguments defaults to scan mode" (fun () ->
        // Dry run is the default and requires no flag. Running the tool with
        // no arguments must never be able to modify anything.
        let options = assertOk (parse []) "empty argument list"
        assertEqual Scan options.Mode "scan is the default"
        assertFalse options.Force "force is off by default")

    test "--scan selects scan mode" (fun () ->
        assertEqual Scan (assertOk (parse [ "--scan" ]) "scan flag").Mode "scan mode")

    test "--delete selects delete mode" (fun () ->
        assertEqual Delete (assertOk (parse [ "--delete" ]) "delete flag").Mode "delete mode")

    test "--dry-run forces scan mode even alongside --delete" (fun () ->
        // An explicit dry-run request must win over a delete request; the
        // safer interpretation of a contradictory command line is the correct one.
        let options = assertOk (parse [ "--delete"; "--dry-run" ]) "contradictory flags"
        assertEqual Scan options.Mode "dry-run wins")

    test "--help selects help mode" (fun () ->
        assertEqual Help (assertOk (parse [ "--help" ]) "help flag").Mode "help mode")

    test "report paths are captured" (fun () ->
        let options = assertOk (parse [ "--json"; "/tmp/r.json"; "--html"; "/tmp/r.html" ]) "report paths"
        assertEqual (Some "/tmp/r.json") options.JsonPath "json path"
        assertEqual (Some "/tmp/r.html") options.HtmlPath "html path"
        assertEqual None options.MarkdownPath "markdown not requested")

    test "numeric options are parsed" (fun () ->
        let options = assertOk (parse [ "--age"; "60"; "--min-size"; "2048"; "--threads"; "4" ]) "numeric options"
        assertEqual (Some 60) options.AgeDays "age"
        assertEqual (Some 2048L) options.MinSizeBytes "min size"
        assertEqual (Some 4) options.Threads "threads")

    test "boolean flags are captured" (fun () ->
        let options = assertOk (parse [ "--duplicates"; "--large-files"; "--verbose"; "--force" ]) "boolean flags"
        assertTrue options.Duplicates "duplicates"
        assertTrue options.LargeFiles "large files"
        assertTrue options.Verbose "verbose"
        assertTrue options.Force "force")

    test "an unknown flag is an error naming the flag" (fun () ->
        let errors = assertError (parse [ "--nonsense" ]) "unknown flag"
        assertContains "--nonsense" (List.head errors) "names the flag")

    test "a flag missing its value is an error" (fun () ->
        let errors = assertError (parse [ "--json" ]) "missing value"
        assertContains "--json" (List.head errors) "names the flag"
        assertContains "value" (List.head errors) "explains what is missing")

    test "a non-numeric value for a numeric flag is an error" (fun () ->
        let errors = assertError (parse [ "--threads"; "many" ]) "bad number"
        assertContains "many" (List.head errors) "names the offending value")

    test "every error is reported at once" (fun () ->
        let errors = assertError (parse [ "--nonsense"; "--threads"; "many" ]) "two problems"
        assertEqual 2 errors.Length "both errors reported")

    test "help text documents every supported flag" (fun () ->
        for flag in [ "--scan"; "--delete"; "--force"; "--dry-run"; "--json"; "--markdown"
                      "--html"; "--duplicates"; "--large-files"; "--age"; "--min-size"
                      "--threads"; "--hash-algorithm"; "--config"; "--allow-root"; "--verbose"; "--help" ] do
            assertContains flag helpText (sprintf "%s is documented" flag))
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add the new source module and its test to the load list, keeping Canonical load order, then add `TestCli.run ()` before the `exit` line. Source `#load`s come first, then `TestFramework.fsx`, then `#load "TestCli.fsx"`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx`

Expected: FAIL — cannot find `../src/Cli.fsx`.

- [ ] **Step 4: Write Cli.fsx**

Create `src/Cli.fsx`:

```fsharp
(**
    Command-line parsing.

    Hand-rolled rather than taking a dependency: the surface is small and the
    project forbids third-party packages.

    Two safety properties are enforced here. Scan is the default mode, so
    invoking the tool with no arguments cannot modify anything. And --dry-run
    overrides --delete, so the safer reading of a contradictory command line
    always wins.

    Errors accumulate: a command line with three mistakes reports all three.
*)
module Cli

open System

/// What the tool was asked to do.
type Mode =
    | Scan
    | Delete
    | Help

/// Parsed command-line options.
type Options =
    { Mode: Mode
      JsonPath: string option
      MarkdownPath: string option
      HtmlPath: string option
      ConfigPath: string option
      Duplicates: bool
      LargeFiles: bool
      AgeDays: int option
      MinSizeBytes: int64 option
      Threads: int option
      HashAlgorithm: string option
      /// Widens the risk gate to include ProbablySafe. Never bypasses the veto.
      Force: bool
      AllowRoot: bool
      Verbose: bool }

/// Options with nothing requested: a plain dry-run scan.
let private empty: Options =
    { Mode = Scan
      JsonPath = None
      MarkdownPath = None
      HtmlPath = None
      ConfigPath = None
      Duplicates = false
      LargeFiles = false
      AgeDays = None
      MinSizeBytes = None
      Threads = None
      HashAlgorithm = None
      Force = false
      AllowRoot = false
      Verbose = false }

/// Usage text. Every flag the parser accepts must appear here; a test enforces it.
let helpText =
    """DiskCleanupAdvisor — find and safely reclaim disk space

USAGE
    dotnet fsi DiskCleanupAdvisor.fsx [options]

MODES
    --scan                  Scan and report. This is the default.
    --delete                Move Safe-rated items to Trash, after confirmation.
    --dry-run               Force scan mode. Overrides --delete.
    --help                  Show this text.

REPORTS
    --json <path>           Write a JSON report.
    --markdown <path>       Write a Markdown report.
    --html <path>           Write a self-contained HTML report.

SCAN OPTIONS
    --duplicates            Detect duplicate files.
    --large-files           Report the largest files.
    --age <days>            Only consider items older than this.
    --min-size <bytes>      Size floor for hashing and large-file reporting.
    --threads <n>           Traversal parallelism.
    --hash-algorithm <alg>  sha256 (default) or md5.
    --config <path>         Configuration file.

SAFETY
    --force                 Also permit ProbablySafe items. Never bypasses the
                            protected-path veto, which has no override.
    --allow-root            Permit running with elevated privileges.
    --verbose               Debug logging to stderr.

NOTES
    Nothing is deleted without --delete and an interactive confirmation.
    Deletion moves items to the Trash; it never unlinks. Space is reclaimed
    only when you empty the Trash yourself."""

/// Parses arguments, accumulating every error.
let parse (args: string list) : Result<Options, string list> =
    let errors = ResizeArray<string>()

    /// Reads the value following a flag, recording an error when absent.
    let readValue (flag: string) (rest: string list) : string option * string list =
        match rest with
        | value :: remaining when not (value.StartsWith "--") -> Some value, remaining
        | _ ->
            errors.Add(sprintf "%s requires a value" flag)
            None, rest

    let parseInt (flag: string) (text: string) : int option =
        match Int32.TryParse text with
        | true, n -> Some n
        | _ ->
            errors.Add(sprintf "%s expects a number, got '%s'" flag text)
            None

    let parseInt64 (flag: string) (text: string) : int64 option =
        match Int64.TryParse text with
        | true, n -> Some n
        | _ ->
            errors.Add(sprintf "%s expects a number, got '%s'" flag text)
            None

    // Tracked separately so --dry-run can override --delete regardless of order.
    let mutable explicitDryRun = false

    let rec loop (options: Options) (remaining: string list) : Options =
        match remaining with
        | [] -> options
        | flag :: rest ->
            match flag with
            | "--scan" -> loop { options with Mode = Scan } rest
            | "--delete" -> loop { options with Mode = Delete } rest
            | "--dry-run" ->
                explicitDryRun <- true
                loop options rest
            | "--help" | "-h" -> loop { options with Mode = Help } rest
            | "--duplicates" -> loop { options with Duplicates = true } rest
            | "--large-files" -> loop { options with LargeFiles = true } rest
            | "--force" -> loop { options with Force = true } rest
            | "--allow-root" -> loop { options with AllowRoot = true } rest
            | "--verbose" | "-v" -> loop { options with Verbose = true } rest
            | "--json" ->
                let value, next = readValue flag rest
                loop { options with JsonPath = value } next
            | "--markdown" ->
                let value, next = readValue flag rest
                loop { options with MarkdownPath = value } next
            | "--html" ->
                let value, next = readValue flag rest
                loop { options with HtmlPath = value } next
            | "--config" ->
                let value, next = readValue flag rest
                loop { options with ConfigPath = value } next
            | "--hash-algorithm" ->
                let value, next = readValue flag rest
                loop { options with HashAlgorithm = value |> Option.map (fun v -> v.ToLowerInvariant()) } next
            | "--age" ->
                let value, next = readValue flag rest
                loop { options with AgeDays = value |> Option.bind (parseInt flag) } next
            | "--min-size" ->
                let value, next = readValue flag rest
                loop { options with MinSizeBytes = value |> Option.bind (parseInt64 flag) } next
            | "--threads" ->
                let value, next = readValue flag rest
                loop { options with Threads = value |> Option.bind (parseInt flag) } next
            | unknown ->
                errors.Add(sprintf "unknown option '%s' (try --help)" unknown)
                loop options rest

    let parsed = loop empty args

    // An explicit dry-run request always wins over a delete request.
    let final =
        if explicitDryRun && parsed.Mode = Delete then { parsed with Mode = Scan } else parsed

    if errors.Count > 0 then Error (List.ofSeq errors) else Ok final
```

- [ ] **Step 5: Run the CLI tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 125 tests total.

- [ ] **Step 6: Write Json.fsx**

Create `src/Report/Json.fsx`:

```fsharp
(**
    JSON report.

    Projects the domain types through the DTO layer before serializing, because
    System.Text.Json cannot handle F# discriminated unions. This module is also
    the fixture format for tests and enables diffing two scans.
*)
module Report.Json

open System
open System.Text.Json

open Types

/// Projects one finding into its serialization-safe form.
let private findingToDto (now: DateTime) (f: Scanner.Finding) : Dto.FindingDto =
    { Path = f.Meta.Path
      RuleName = f.RuleName
      Category = Category.toText f.Category
      Risk = RiskLevel.toText f.Risk
      Recoverability = Recoverability.toText f.Recoverability
      Strategy = DeleteStrategy.toText f.Strategy
      SizeBytes = f.Meta.SizeBytes
      AgeDays = (FileMeta.age now f.Meta).TotalDays
      SafeToDelete = f.Candidate.IsSome
      Reason = f.Reason }

/// Projects a whole scan result into its serialization-safe form.
let toDto (result: Scanner.ScanResult) : Dto.ScanResultDto =
    let now = result.GeneratedAtUtc
    let categories =
        Scanner.byCategory result
        |> List.map (fun (category, findings) ->
            { Dto.Category = Category.toText category
              Dto.ItemCount = findings.Length
              Dto.TotalBytes = findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
              Dto.ReclaimableBytes =
                findings
                |> List.filter (fun f -> f.Candidate.IsSome)
                |> List.sumBy (fun f -> f.Meta.SizeBytes) })

    { GeneratedAtUtc = result.GeneratedAtUtc.ToString "o"
      Platform = sprintf "%A" result.Platform
      ScanRoots = result.ScanRoots
      TotalItems = result.Findings.Length
      TotalBytes = result.Findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
      ReclaimableBytes = Scanner.reclaimableBytes result
      PendingReviewBytes = Scanner.pendingReviewBytes result
      Categories = categories
      Findings = result.Findings |> List.map (findingToDto now)
      Errors = result.Errors |> List.map FsError.toText
      UnreadableDirectoryCount = Scanner.unreadableDirectoryCount result
      ElapsedSeconds = result.Elapsed.TotalSeconds }

/// Writes a JSON report to disk.
let write (path: string) (result: Scanner.ScanResult) : Result<unit, string> =
    try
        let json = JsonSerializer.Serialize(toDto result, Dto.serializerOptions)
        let directory = IO.Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (IO.Directory.Exists directory) then
            IO.Directory.CreateDirectory directory |> ignore
        IO.File.WriteAllText(path, json)
        Ok ()
    with ex -> Error (sprintf "cannot write %s: %s" path ex.Message)
```

- [ ] **Step 7: Write Console.fsx**

Create `src/Report/Console.fsx`:

```fsharp
(**
    Console report — the primary interface.

    Two figures are always presented separately: bytes that can be reclaimed
    automatically, and bytes surfaced for review. Merging them would let a user
    believe space had been freed when it had only been listed.

    Color degrades automatically when stdout is redirected or NO_COLOR is set.
*)
module Report.Console

open System
open System.Text

open Types
open Utilities

/// True when ANSI color is appropriate for this stdout.
let private useColor () =
    not Console.IsOutputRedirected
    && String.IsNullOrEmpty(Environment.GetEnvironmentVariable "NO_COLOR")

let private reset = "\027[0m"
let private bold = "\027[1m"
let private dim = "\027[90m"

/// Wraps text in a color when color is enabled.
let private colorize (color: string) (text: string) =
    if useColor () then color + text + reset else text

/// Renders the risk icon and label for a finding.
let private riskBadge (level: RiskLevel) =
    let d = RiskLevel.describe level
    colorize d.AnsiColor (sprintf "%s %-12s" d.Icon (RiskLevel.toText level))

/// Renders a horizontal rule.
let private rule () = String.replicate 72 "─"

/// Renders the complete console report.
let render (result: Scanner.ScanResult) : string =
    let sb = StringBuilder()
    let append (text: string) = sb.AppendLine text |> ignore

    let reclaimable = Scanner.reclaimableBytes result
    let pending = Scanner.pendingReviewBytes result

    append ""
    append (colorize bold "DiskCleanupAdvisor")
    append (rule ())
    append (sprintf "Platform     %A" result.Platform)
    append (sprintf "Scanned      %d roots, %d items" result.ScanRoots.Length result.Findings.Length)
    append (sprintf "Duration     %s" (Format.duration result.Elapsed))
    append ""

    // The two figures, always distinct.
    append (colorize bold "Space")
    append (sprintf "  Reclaimable      %s   %s"
                (colorize "\027[32m" (Format.bytes reclaimable))
                (colorize dim "eligible for automatic removal"))
    append (sprintf "  Pending review   %s   %s"
                (colorize "\027[33m" (Format.bytes pending))
                (colorize dim "requires your judgment"))
    append ""

    // Per-category breakdown, largest first.
    let grouped = Scanner.byCategory result
    if List.isEmpty grouped then
        append (colorize dim "  Nothing found.")
    else
        append (colorize bold "By category")
        append (sprintf "  %-26s %10s %10s %8s" "CATEGORY" "TOTAL" "RECLAIM" "ITEMS")
        for category, findings in grouped do
            let total = findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
            let claimable =
                findings |> List.filter (fun f -> f.Candidate.IsSome) |> List.sumBy (fun f -> f.Meta.SizeBytes)
            append (sprintf "  %-26s %10s %10s %8d"
                        (Category.toText category)
                        (Format.bytes total)
                        (Format.bytes claimable)
                        findings.Length)
    append ""

    // The ten largest individual findings.
    let largest =
        result.Findings
        |> List.sortByDescending (fun f -> f.Meta.SizeBytes)
        |> List.truncate 10
    if not (List.isEmpty largest) then
        append (colorize bold "Largest items")
        for f in largest do
            append (sprintf "  %s %10s  %s" (riskBadge f.Risk) (Format.bytes f.Meta.SizeBytes) f.Meta.Path)
        append ""

    // Diagnostics.
    append (colorize bold "Diagnostics")
    append (Diagnostics.render result.Counters)
    append ""

    // The Full Disk Access hint — actionable, and the difference between a
    // complete scan and a silently partial one.
    let unreadable = Scanner.unreadableDirectoryCount result
    if unreadable > 0 then
        append (colorize "\027[33m" (sprintf "  %d directories were unreadable." unreadable))
        append (colorize dim "  Grant Full Disk Access to your terminal in System Settings →")
        append (colorize dim "  Privacy & Security to include them in the scan.")
        append ""

    if reclaimable > 0L then
        append (colorize dim "  This was a dry run. Nothing was modified.")
        append (colorize dim "  Run with --delete to move the reclaimable items to Trash.")
        append ""

    sb.ToString()
```

- [ ] **Step 8: Write the entry point**

Create `DiskCleanupAdvisor.fsx`:

```fsharp
(**
    DiskCleanupAdvisor — entry point.

    Finds disposable files, explains why each is disposable, and — only when
    explicitly asked — moves them to the Trash. Never unlinks.

    Usage:  dotnet fsi DiskCleanupAdvisor.fsx -- [options]
            dotnet fsi DiskCleanupAdvisor.fsx -- --help

    This file is composition only. All behavior lives in src/.
*)

#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/RiskEngine.fsx"
#load "src/FileClassifier.fsx"
#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/RiskEngine.fsx"
#load "src/FileClassifier.fsx"
#load "src/DirectoryWalker.fsx"
#load "src/Scanner.fsx"
#load "src/Report/Dto.fsx"
#load "src/Report/Json.fsx"
#load "src/Report/Console.fsx"
#load "src/Cli.fsx"

open System

/// Prints a list of errors to stderr and returns a failure exit code.
let private fail (errors: string list) : int =
    eprintfn "Error:"
    for e in errors do eprintfn "  %s" e
    1

/// Applies command-line overrides on top of the loaded configuration.
/// The command line wins over the file, which wins over the defaults.
let private mergeOptions (options: Cli.Options) (config: Configuration.Config) : Configuration.Config =
    { config with
        AgeThresholdDays = defaultArg options.AgeDays config.AgeThresholdDays
        MinSizeBytes = defaultArg options.MinSizeBytes config.MinSizeBytes
        ThreadCount = defaultArg options.Threads config.ThreadCount
        HashAlgorithm = defaultArg options.HashAlgorithm config.HashAlgorithm }

/// Runs a scan and emits every requested report.
let private runScan (options: Cli.Options) (config: Configuration.Config) (log: Logging.Logger) : int =
    let fs = Platform.FileSystem.real
    let platform = Platform.current ()

    log |> Logging.info (sprintf "starting scan on %A" platform)

    let rules = Rules.resolveAll fs platform
    log |> Logging.info (sprintf "%d rules resolved on this machine" rules.Length)

    if List.isEmpty rules then
        eprintfn "No rules resolved on this machine. Nothing to scan."
        0
    else

    match Scanner.scan fs config options.Force rules with
    | Error errors -> fail errors
    | Ok result ->
        log |> Logging.info (sprintf "scan complete: %d findings" result.Findings.Length)

        // Console output always.
        printf "%s" (Report.Console.render result)

        // File reports on request. A write failure is reported but does not
        // discard the scan that already succeeded.
        let writeFailures =
            [ match options.JsonPath with
              | Some path ->
                  match Report.Json.write path result with
                  | Ok () -> log |> Logging.info (sprintf "wrote JSON report to %s" path)
                  | Error e -> yield e
              | None -> () ]

        if List.isEmpty writeFailures then 0 else fail writeFailures

/// Entry point. `fsi.CommandLineArgs` includes the script name at index 0.
let private main () : int =
    let args = fsi.CommandLineArgs |> Array.toList |> List.skip 1

    match Cli.parse args with
    | Error errors ->
        eprintfn "%s" Cli.helpText
        eprintfn ""
        fail errors
    | Ok options ->

    match options.Mode with
    | Cli.Help ->
        printfn "%s" Cli.helpText
        0
    | Cli.Delete ->
        // Cleaner does not exist yet (Task 15). Until it does, the tool is
        // structurally incapable of modifying anything, so --delete is
        // refused explicitly rather than silently ignored.
        eprintfn "--delete is not yet implemented. Run without it to scan."
        1
    | Cli.Scan ->
        let log =
            Logging.create (if options.Verbose then Logging.Debug else Logging.Info)

        if Platform.isElevated () && not options.AllowRoot then
            fail [ "refusing to run with elevated privileges; pass --allow-root to override" ]
        else
            match Configuration.load Platform.FileSystem.real options.ConfigPath with
            | Error errors -> fail errors
            | Ok config -> runScan options (mergeOptions options config) log

exit (main ())
```

- [ ] **Step 9: Run the full test suite**

Run: `dotnet fsi tests/Tests.fsx`

Expected: PASS — 125 tests.

- [ ] **Step 10: Verify --help works**

Run: `dotnet fsi DiskCleanupAdvisor.fsx -- --help`

Expected: the usage text, exit code 0. Verify with `echo "exit=$?"`.

- [ ] **Step 11: Run a real scan — the first end-to-end verification**

Run: `dotnet fsi DiskCleanupAdvisor.fsx -- --scan`

Expected: a full console report showing platform, per-category breakdown, largest items, diagnostics, and — on a machine without Full Disk Access — the unreadable-directories hint.

Confirm all of the following before proceeding:
- The two space figures are shown **separately** and are not equal by accident.
- The dry-run notice appears at the end.
- No file was modified: `git status --short` shows only expected changes.

- [ ] **Step 12: Verify the JSON report and the stdout/stderr split**

```bash
dotnet fsi DiskCleanupAdvisor.fsx -- --scan --json /tmp/report.json > /dev/null
python3 -c "import json;d=json.load(open('/tmp/report.json'));print('items',d['totalItems']);print('reclaimable',d['reclaimableBytes']);print('pendingReview',d['pendingReviewBytes'])"
rm /tmp/report.json
```

Expected: valid JSON parses, and `reclaimableBytes` and `pendingReviewBytes` are distinct fields. If `json.load` raises, the DTO layer has let a DU through.

- [ ] **Step 13: Commit**

```bash
git add src/Report/Json.fsx src/Report/Console.fsx src/Cli.fsx DiskCleanupAdvisor.fsx tests/TestCli.fsx tests/Tests.fsx
git commit -m "feat: add console and JSON reports, CLI, and entry point

First end-to-end usable version: dotnet fsi DiskCleanupAdvisor.fsx
performs a real scan and reports. Scan is the default mode and
--dry-run overrides --delete, so a contradictory command line resolves
to the safer reading. --delete is refused explicitly until Cleaner
exists, rather than silently ignored.

Reclaimable and pending-review space are always presented as two
separate figures."
```

---

### Task 14: Hashing and duplicate detection

**Files:**
- Create: `src/Hashing.fsx`, `src/Duplicates.fsx`
- Create: `tests/TestDuplicates.fsx`
- Modify: `tests/Tests.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`, `Platform`
- Produces:
  - `Hashing.Algorithm` = `Sha256 | Md5`
  - `Hashing.parse : string -> Result<Algorithm, string>`
  - `Hashing.full : FileSystemOps -> Algorithm -> string -> Result<string, FsError>`
  - `Hashing.sampled : FileSystemOps -> string -> int64 -> Result<string, FsError>`
  - `Duplicates.DuplicateGroup` = `{ Hash: string; SizeBytes: int64; Paths: string list; Keeper: string; Wasted: int64 }`
  - `Duplicates.find : FileSystemOps -> Algorithm -> minSize: int64 -> FileMeta list -> DuplicateGroup list`

**The funnel.** Hashing a home directory naively means reading hundreds of gigabytes. Three stages avoid that:
1. Group by exact byte size — `stat` only, no reads. Singleton groups drop out, eliminating ~95% of candidates.
2. Sampled hash on survivors: `first 64 KB ‖ last 64 KB ‖ size`. Two reads per file regardless of size.
3. Full streaming hash only where stage 2 still collides.

**Duplicates are `Review`, never `Safe`.** Identical bytes do not imply a copy is disposable — the second copy may be the one a running program references. They are excluded from `--delete` unless `--force`.

- [ ] **Step 1: Write the failing tests**

Create `tests/TestDuplicates.fsx`:

```fsharp
open System
open TestFramework
open Types
open Platform

let private entry path size days =
    { Path = path; SizeBytes = size; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = days }

let private metaOf (fs: FileSystemOps) path =
    match fs.GetInfo path with
    | Ok m -> m
    | Error e -> failwithf "test tree is wrong: %A" e

let run () =
    suite "Duplicates"

    // The in-memory filesystem returns zero-filled content sized to the entry,
    // so files of equal size hash identically and files of unequal size do not.
    let tree =
        [ entry "/a.bin" 4096L 10.0
          entry "/b.bin" 4096L 5.0
          entry "/c.bin" 4096L 20.0
          entry "/d.bin" 8192L 1.0
          entry "/small1.txt" 10L 1.0
          entry "/small2.txt" 10L 1.0 ]
    let fs = FileSystem.inMemory tree
    let metas = tree |> List.map (fun e -> metaOf fs e.Path)

    test "files of identical size and content group together" (fun () ->
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertEqual 1 groups.Length "one duplicate group"
        assertEqual 3 (List.head groups).Paths.Length "three identical 4096-byte files")

    test "a file with a unique size is never a duplicate" (fun () ->
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertFalse (groups |> List.exists (fun g -> g.Paths |> List.contains "/d.bin")) "unique size excluded")

    test "files below the size floor are ignored" (fun () ->
        // Duplicate 10-byte files are not worth the I/O to confirm.
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertFalse (groups |> List.exists (fun g -> g.Paths |> List.exists (fun p -> p.Contains "small")))
            "small files excluded by the floor")

    test "the oldest path is chosen as the keeper" (fun () ->
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertEqual "/c.bin" (List.head groups).Keeper "the 20-day-old file is kept")

    test "wasted bytes count every copy except the keeper" (fun () ->
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertEqual 8192L (List.head groups).Wasted "two redundant copies of 4096 bytes")

    test "an empty input yields no groups" (fun () ->
        assertEqual 0 (Duplicates.find fs Hashing.Sha256 1024L []).Length "no input, no groups")

    suite "Hashing"

    test "algorithm names parse case-insensitively" (fun () ->
        assertEqual Hashing.Sha256 (assertOk (Hashing.parse "SHA256") "sha256") "sha256"
        assertEqual Hashing.Md5 (assertOk (Hashing.parse "md5") "md5") "md5"
        assertError (Hashing.parse "crc32") "unsupported algorithm" |> ignore)

    test "hashing the same content twice yields the same digest" (fun () ->
        let h1 = assertOk (Hashing.full fs Hashing.Sha256 "/a.bin") "first hash"
        let h2 = assertOk (Hashing.full fs Hashing.Sha256 "/b.bin") "second hash"
        assertEqual h1 h2 "identical content hashes identically")

    test "hashing a missing file returns an error rather than throwing" (fun () ->
        assertError (Hashing.full fs Hashing.Sha256 "/nope") "missing file" |> ignore)
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add `../src/Hashing.fsx` and `../src/Duplicates.fsx` to the source load list in Canonical load order, then `#load "TestDuplicates.fsx"` and `TestDuplicates.run ()`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx` → FAIL, `Hashing` is not defined.

- [ ] **Step 4: Write Hashing.fsx**

```fsharp
(**
    File hashing.

    Two modes: a full streaming hash, and a sampled hash that reads only the
    first and last 64 KB plus the file length. The sampled form is what makes
    duplicate detection affordable — it costs two reads regardless of whether
    the file is 2 MB or 2 GB.

    A sampled hash can collide for genuinely different files, so it is only
    ever used to narrow candidates. A full hash always confirms.
*)
module Hashing

open System
open System.IO
open System.Security.Cryptography

/// Supported digest algorithms.
type Algorithm =
    | Sha256
    | Md5

/// Parses an algorithm name from configuration or the command line.
let parse (text: string) : Result<Algorithm, string> =
    match text.Trim().ToLowerInvariant() with
    | "sha256" -> Ok Sha256
    | "md5" -> Ok Md5
    | other -> Error (sprintf "unsupported hash algorithm '%s' (expected sha256 or md5)" other)

/// Creates a fresh hash implementation. The caller disposes it.
let private create (algorithm: Algorithm) : HashAlgorithm =
    match algorithm with
    | Sha256 -> SHA256.Create() :> HashAlgorithm
    | Md5 -> MD5.Create() :> HashAlgorithm

/// Renders a digest as lowercase hex.
let private toHex (bytes: byte[]) : string =
    Convert.ToHexString(bytes).ToLowerInvariant()

/// How many bytes are read from each end for a sampled hash.
let private sampleWindow = 65_536

/// Streams a file through the chosen algorithm and returns a hex digest.
let full (fs: Platform.FileSystemOps) (algorithm: Algorithm) (path: string) : Result<string, Types.FsError> =
    match fs.OpenRead path with
    | Error e -> Error e
    | Ok stream ->
        use stream = stream
        use hasher = create algorithm
        try Ok (toHex (hasher.ComputeHash stream))
        with ex -> Error (Types.IoFailure(path, ex.Message))

/// Hashes the first and last 64 KB plus the file length.
///
/// Cheap enough to run across every same-size candidate. Files smaller than
/// two windows are hashed in full, since sampling would read them anyway.
let sampled (fs: Platform.FileSystemOps) (path: string) (sizeBytes: int64) : Result<string, Types.FsError> =
    match fs.OpenRead path with
    | Error e -> Error e
    | Ok stream ->
        use stream = stream
        use hasher = SHA256.Create()
        try
            if sizeBytes <= int64 sampleWindow * 2L then
                Ok (toHex (hasher.ComputeHash stream))
            else
                let head = Array.zeroCreate sampleWindow
                let tail = Array.zeroCreate sampleWindow
                stream.ReadExactly(head, 0, sampleWindow)
                stream.Seek(-int64 sampleWindow, SeekOrigin.End) |> ignore
                stream.ReadExactly(tail, 0, sampleWindow)
                // Length participates in the digest so two files sharing both
                // windows but differing in size cannot collide.
                let lengthBytes = BitConverter.GetBytes sizeBytes
                let combined = Array.concat [ head; tail; lengthBytes ]
                Ok (toHex (hasher.ComputeHash combined))
        with ex -> Error (Types.IoFailure(path, ex.Message))
```

- [ ] **Step 5: Write Duplicates.fsx**

```fsharp
(**
    Duplicate detection via a three-stage funnel.

      1. Group by exact byte size — stat only, no reads. Singletons drop out,
         which eliminates roughly 95% of candidates on a real machine.
      2. Sampled hash on survivors — two reads per file regardless of size.
      3. Full hash only where the sampled hash still collides.

    Duplicates are reported at risk level Review and are never Safe. Identical
    bytes do not make a copy disposable: the second copy may be the one a
    running program references, or the one inside a backup the user relies on.
*)
module Duplicates

open System

/// A set of files with identical content.
type DuplicateGroup =
    { Hash: string
      SizeBytes: int64
      Paths: string list
      /// The copy to keep: oldest first, then shallowest, then alphabetical.
      Keeper: string
      /// Bytes consumed by every copy except the keeper.
      Wasted: int64 }

/// Chooses which copy to keep.
///
/// Oldest wins, on the reasoning that the earliest copy is most likely the
/// original. Ties break toward the shallowest path, then alphabetically, so
/// the choice is deterministic across runs.
let private chooseKeeper (metas: Types.FileMeta list) : string =
    metas
    |> List.sortBy (fun m ->
        m.LastModified,
        (Utilities.Path.segments m.Path).Length,
        m.Path)
    |> List.head
    |> fun m -> m.Path

/// Groups a list of items by a fallible key, discarding items whose key
/// could not be computed and groups with only one member.
let private groupByHash
    (computeKey: Types.FileMeta -> Result<string, Types.FsError>)
    (items: Types.FileMeta list)
    : (string * Types.FileMeta list) list =
    items
    |> List.choose (fun meta ->
        match computeKey meta with
        | Ok key -> Some (key, meta)
        | Error _ -> None)          // unreadable files simply are not compared
    |> List.groupBy fst
    |> List.map (fun (key, pairs) -> key, pairs |> List.map snd)
    |> List.filter (fun (_, group) -> group.Length > 1)

/// Finds groups of byte-identical files.
let find
    (fs: Platform.FileSystemOps)
    (algorithm: Hashing.Algorithm)
    (minSizeBytes: int64)
    (files: Types.FileMeta list)
    : DuplicateGroup list =

    files
    // Stage 1 — exact size. Free: no file is opened.
    |> List.filter (fun m -> not m.IsDirectory && m.SizeBytes >= minSizeBytes)
    |> List.groupBy (fun m -> m.SizeBytes)
    |> List.filter (fun (_, group) -> group.Length > 1)
    |> List.collect (fun (size, sameSize) ->
        // Stage 2 — sampled hash. Two reads per file.
        sameSize
        |> groupByHash (fun m -> Hashing.sampled fs m.Path m.SizeBytes)
        |> List.collect (fun (_, sampledMatch) ->
            // Stage 3 — full hash, only for files that survived stage 2.
            sampledMatch
            |> groupByHash (fun m -> Hashing.full fs algorithm m.Path)
            |> List.map (fun (hash, confirmed) ->
                { Hash = hash
                  SizeBytes = size
                  Paths = confirmed |> List.map (fun m -> m.Path) |> List.sort
                  Keeper = chooseKeeper confirmed
                  Wasted = size * int64 (confirmed.Length - 1) })))
    |> List.sortByDescending (fun g -> g.Wasted)
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx` → PASS, 135 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Hashing.fsx src/Duplicates.fsx tests/TestDuplicates.fsx tests/Tests.fsx
git commit -m "feat: add hashing and three-stage duplicate funnel

Group by exact size (no reads), then sampled hash of head+tail+length
(two reads per file regardless of size), then full hash only on
surviving collisions. Duplicates are always Review, never Safe:
identical bytes do not make a copy disposable."
```

---

### Task 15: Cleaner and the `--delete` path

**This task is last among the functional work for a reason.** Until it exists, the tool is structurally incapable of modifying anything. Do not start it until every RiskEngine gate test from Task 8 passes.

**Files:**
- Create: `src/Cleaner.fsx`
- Create: `tests/TestCleaner.fsx`
- Modify: `tests/Tests.fsx`, `DiskCleanupAdvisor.fsx`

**Interfaces:**
- Consumes: `Types`, `Utilities`, `Platform`, `RiskEngine`, `Logging`
- Produces:
  - `Cleaner.TrashResult` = `{ Moved: string list; Failed: (string * FsError) list; BytesMoved: int64 }`
  - `Cleaner.trashRoot : unit -> Result<string, string>`
  - `Cleaner.journalPath : DateTime -> string`
  - `Cleaner.writeJournal : string -> DeletionCandidate list -> Result<unit, string>`
  - `Cleaner.moveToTrash : FileSystemOps -> Logger -> DeletionCandidate list -> TrashResult`

**There is no `File.Delete` call anywhere in this module.** Reclaiming space requires the user to empty the Trash themselves. Reports must say so.

**`DeleteStrategy` is honored here, and this is the only place it has effect.** The distinction is real: Xcode recreates `DerivedData` only if the directory still exists, so emptying it and removing it are different outcomes. `RiskEngine.candidateStrategy` exists for exactly this and is otherwise unused.

- `ContentsOnly` — move the children into the Trash, leave the directory in place.
- `MatchingFiles` — move only the individual files the rule matched. This is what the scanner already produces, since it yields file candidates rather than directory candidates, so it needs no special handling.
- `WholeDirectory` — move the directory itself.

Because the scanner emits per-file candidates, `ContentsOnly` and `MatchingFiles` behave identically in practice at this stage: the directory is never itself a candidate, so it survives regardless. The strategy is still threaded through and asserted in tests so that a future directory-level candidate cannot silently acquire the wrong behavior.

- [ ] **Step 1: Write the failing tests**

Create `tests/TestCleaner.fsx`:

```fsharp
open System
open TestFramework
open Types

let run () =
    suite "Cleaner"

    test "the trash root resolves on this platform" (fun () ->
        let root = assertOk (Cleaner.trashRoot ()) "trash root resolves"
        assertTrue (root.Length > 0) "non-empty path")

    test "the journal path is timestamped and lands under reports" (fun () ->
        let path = Cleaner.journalPath (DateTime(2026, 7, 20, 13, 45, 0))
        assertContains "2026" path "year present"
        assertContains "deletions" path "named for its purpose"
        assertContains ".json" path "json extension")

    test "a name collision in the trash is resolved rather than overwriting" (fun () ->
        // Overwriting an existing trashed file would destroy the earlier one,
        // which is the exact outcome this tool exists to prevent.
        let taken = Set.ofList [ "/trash/a.txt"; "/trash/a.txt.1" ]
        let chosen = Cleaner.uniqueDestination (fun p -> taken.Contains p) "/trash" "a.txt"
        assertFalse (taken.Contains chosen) "chosen name is free"
        assertContains "a.txt" chosen "derived from the original name")

    test "an unused name is returned unchanged" (fun () ->
        let chosen = Cleaner.uniqueDestination (fun _ -> false) "/trash" "b.txt"
        assertContains "b.txt" chosen "original name kept")

    test "a ContentsOnly candidate never targets the directory itself" (fun () ->
        // Xcode recreates DerivedData only if the directory still exists, so
        // emptying it and removing it are materially different outcomes.
        let rule =
            { Rules.Name = "Test ContentsOnly"
              Rules.Platforms = Set.ofList [ MacOS; Linux; Windows ]
              Rules.Root = Absolute "/derived"
              Rules.Include = []; Rules.Exclude = []
              Rules.Risk = Safe; Rules.Recoverability = Regenerated
              Rules.Strategy = ContentsOnly
              Rules.MinAge = None; Rules.MinSize = None
              Rules.SafeToDelete = true
              Rules.Description = "t"; Rules.Reason = "t"
              Rules.EstimatedBenefit = "t"; Rules.Examples = [ "/derived" ]
              Rules.Category = DeveloperArtifacts }
        let resolved = { Rules.Rule = rule; Rules.Root = "/derived" }
        let child =
            { Path = "/derived/build/x.o"; SizeBytes = 10L
              LastAccess = DateTime.UtcNow; LastModified = DateTime.UtcNow
              Created = DateTime.UtcNow; IsDirectory = false
              LinkTarget = None; Owner = "u"; Mode = "rw-r--r--" }
        let candidate = assertOk (RiskEngine.evaluate false resolved child) "child accepted"
        assertEqual ContentsOnly (RiskEngine.candidateStrategy candidate) "strategy carried through"
        assertFalse (RiskEngine.candidatePath candidate = "/derived") "the root is never the target")
```

- [ ] **Step 2: Wire into the test entry point**

Modify `tests/Tests.fsx` — add `../src/Cleaner.fsx` in Canonical load order, then `#load "TestCleaner.fsx"` and `TestCleaner.run ()`.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet fsi tests/Tests.fsx` → FAIL, `Cleaner` is not defined.

- [ ] **Step 4: Write Cleaner.fsx**

```fsharp
(**
    Moves items to the platform Trash.

    **This module contains no File.Delete, Directory.Delete, or any other
    unlink call, and must never acquire one.** Every removal is a relocation
    into the Trash, which the user empties themselves. That is what makes every
    action this tool takes reversible.

    A consequence worth stating in reports: space is not reclaimed at the moment
    this module runs. It is reclaimed when the user empties the Trash.

    Deletions are journaled before the move, not after, so a crash mid-run
    leaves an accurate record of what was attempted.
*)
module Cleaner

open System
open System.IO
open System.Text.Json

/// What a trash operation accomplished.
type TrashResult =
    { Moved: string list
      Failed: (string * Types.FsError) list
      BytesMoved: int64 }

/// One journal entry, written before its move is attempted.
type private JournalEntry =
    { Path: string
      RuleName: string
      SizeBytes: int64
      AttemptedAtUtc: string }

/// Resolves the platform Trash directory.
let trashRoot () : Result<string, string> =
    match Platform.current () with
    | Types.MacOS ->
        Ok (Path.Combine(Platform.home (), ".Trash"))
    | Types.Linux ->
        // XDG Trash specification.
        let dataHome =
            match Environment.GetEnvironmentVariable "XDG_DATA_HOME" with
            | null | "" -> Path.Combine(Platform.home (), ".local", "share")
            | value -> value
        Ok (Path.Combine(dataHome, "Trash", "files"))
    | Types.Windows ->
        // Moving to the Recycle Bin requires SHFileOperation, which this
        // script-only project cannot P/Invoke portably. Refuse rather than
        // silently unlinking — an irreversible fallback would defeat the
        // entire safety model.
        Error "moving to the Recycle Bin is not supported on Windows in this version; run without --delete"

/// Where the deletion journal for a run is written.
let journalPath (timestamp: DateTime) : string =
    let name = sprintf "deletions-%s.json" (timestamp.ToString "yyyy-MM-dd-HHmmss")
    Path.Combine("reports", name)

/// Chooses a destination filename that does not already exist.
///
/// `exists` is passed in rather than called directly so the collision logic
/// can be tested without touching the filesystem.
let uniqueDestination (exists: string -> bool) (directory: string) (fileName: string) : string =
    let candidate = Path.Combine(directory, fileName)
    if not (exists candidate) then candidate
    else
        let stem = Path.GetFileNameWithoutExtension fileName
        let extension = Path.GetExtension fileName
        let rec attempt (n: int) =
            let next = Path.Combine(directory, sprintf "%s.%d%s" stem n extension)
            if exists next then attempt (n + 1) else next
        attempt 1

/// Writes the journal for a set of candidates. Called before any move.
let writeJournal (path: string) (candidates: RiskEngine.DeletionCandidate list) : Result<unit, string> =
    try
        let entries =
            candidates
            |> List.map (fun c ->
                { Path = RiskEngine.candidatePath c
                  RuleName = RiskEngine.candidateRule c
                  SizeBytes = RiskEngine.candidateSize c
                  AttemptedAtUtc = DateTime.UtcNow.ToString "o" })
        let directory = Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (Directory.Exists directory) then
            Directory.CreateDirectory directory |> ignore
        File.WriteAllText(path, JsonSerializer.Serialize(entries, Dto.serializerOptions))
        Ok ()
    with ex -> Error (sprintf "cannot write the deletion journal to %s: %s" path ex.Message)

/// Moves every candidate into the Trash.
///
/// Each item is re-checked immediately before its move: if it changed since
/// the scan, it is skipped rather than moved, because the thing being removed
/// is no longer the thing that was evaluated.
let moveToTrash
    (fs: Platform.FileSystemOps)
    (log: Logging.Logger)
    (candidates: RiskEngine.DeletionCandidate list)
    : TrashResult =

    match trashRoot () with
    | Error message ->
        log |> Logging.error message
        { Moved = []
          Failed = candidates |> List.map (fun c -> RiskEngine.candidatePath c, Types.NotSupported(RiskEngine.candidatePath c, message))
          BytesMoved = 0L }
    | Ok trash ->

    if not (Directory.Exists trash) then Directory.CreateDirectory trash |> ignore

    let moved = ResizeArray<string>()
    let failed = ResizeArray<string * Types.FsError>()
    let mutable bytesMoved = 0L

    for candidate in candidates do
        let source = RiskEngine.candidatePath candidate
        let expectedSize = RiskEngine.candidateSize candidate

        // Re-stat before moving. A file that changed since the scan was not
        // the file the risk engine approved.
        match fs.GetInfo source with
        | Error e ->
            failed.Add(source, e)
        | Ok current when current.SizeBytes <> expectedSize ->
            log |> Logging.warn (sprintf "skipping %s: changed since the scan" source)
            failed.Add(source, Types.IoFailure(source, "changed since the scan"))
        | Ok _ ->
            let destination = uniqueDestination (fun p -> File.Exists p || Directory.Exists p) trash (Path.GetFileName source)
            try
                // Move, never delete. On a cross-device move File.Move copies
                // then removes the source, which is still a relocation.
                if Directory.Exists source then Directory.Move(source, destination)
                else File.Move(source, destination)
                moved.Add source
                bytesMoved <- bytesMoved + expectedSize
                log |> Logging.debug (sprintf "moved %s to %s" source destination)
            with ex ->
                log |> Logging.warn (sprintf "could not move %s: %s" source ex.Message)
                failed.Add(source, Types.IoFailure(source, ex.Message))

    { Moved = List.ofSeq moved
      Failed = List.ofSeq failed
      BytesMoved = bytesMoved }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet fsi tests/Tests.fsx` → PASS, 140 tests.

- [ ] **Step 6: Confirm no unlink call exists anywhere in the project**

```bash
grep -rn "File.Delete\|Directory.Delete\|\.Delete()" src/ DiskCleanupAdvisor.fsx || echo "CLEAN — no unlink calls"
```

Expected: `CLEAN — no unlink calls`. If anything matches, remove it before continuing. This grep is the mechanical check behind the trash-only guarantee.

- [ ] **Step 7: Wire `--delete` into the entry point**

In `DiskCleanupAdvisor.fsx`, add `#load "src/Cleaner.fsx"` in Canonical load order, then replace the `Cli.Delete` branch with:

```fsharp
    | Cli.Delete ->
        let log = Logging.create (if options.Verbose then Logging.Debug else Logging.Info)
        if Platform.isElevated () && not options.AllowRoot then
            fail [ "refusing to run with elevated privileges; pass --allow-root to override" ]
        else
        match Configuration.load Platform.FileSystem.real options.ConfigPath with
        | Error errors -> fail errors
        | Ok config ->
            let config = mergeOptions options config
            let fs = Platform.FileSystem.real
            let rules = Rules.resolveAll fs (Platform.current ())
            match Scanner.scan fs config options.Force rules with
            | Error errors -> fail errors
            | Ok result ->
                printf "%s" (Report.Console.render result)
                let candidates = result.Findings |> List.choose (fun f -> f.Candidate)
                if List.isEmpty candidates then
                    printfn "Nothing is eligible for automatic removal."
                    0
                else
                    let total = candidates |> List.sumBy RiskEngine.candidateSize
                    printfn ""
                    printfn "About to move %d items (%s) to the Trash."
                        candidates.Length (Utilities.Format.bytes total)
                    printfn "They can be restored from the Trash until you empty it."
                    printf  "Type 'yes' to continue: "
                    match Console.ReadLine() with
                    | "yes" ->
                        let journal = Cleaner.journalPath DateTime.Now
                        match Cleaner.writeJournal journal candidates with
                        | Error e -> fail [ e ]
                        | Ok () ->
                            let outcome = Cleaner.moveToTrash fs log candidates
                            printfn ""
                            printfn "Moved %d items (%s) to the Trash."
                                outcome.Moved.Length (Utilities.Format.bytes outcome.BytesMoved)
                            // Stated plainly: the space is not back yet.
                            printfn "Empty the Trash to actually reclaim this space."
                            if not (List.isEmpty outcome.Failed) then
                                printfn "%d items could not be moved; see %s" outcome.Failed.Length journal
                            0
                    | _ ->
                        printfn "Cancelled. Nothing was modified."
                        0
```

- [ ] **Step 8: Verify the confirmation prompt cancels safely**

```bash
echo "no" | dotnet fsi DiskCleanupAdvisor.fsx -- --delete
```

Expected: the report, the prompt, then `Cancelled. Nothing was modified.` Confirm with `ls ~/.Trash | wc -l` before and after that the count is unchanged.

- [ ] **Step 9: Commit**

```bash
git add src/Cleaner.fsx tests/TestCleaner.fsx tests/Tests.fsx DiskCleanupAdvisor.fsx
git commit -m "feat: add trash-only cleaner and the --delete path

No unlink call exists anywhere in the module: every removal is a
relocation into the platform Trash, which the user empties themselves.
Items are re-stat'd immediately before moving and skipped if they
changed since the scan. Deletions are journaled before the move so a
crash leaves an accurate record.

Windows refuses --delete rather than falling back to an irreversible
unlink."
```

---

### Task 16: Markdown and HTML reports

**Files:**
- Create: `src/Report/Markdown.fsx`, `src/Report/Html.fsx`
- Modify: `DiskCleanupAdvisor.fsx`, `tests/Tests.fsx`, `tests/TestReports.fsx`

**Interfaces:**
- Produces:
  - `Report.Markdown.render : ScanResult -> string`
  - `Report.Markdown.write : string -> ScanResult -> Result<unit, string>`
  - `Report.Html.render : ScanResult -> string`
  - `Report.Html.write : string -> ScanResult -> Result<unit, string>`

**HTML constraints:** one self-contained file. Inline CSS and JS. No CDN, no external assets, no web fonts. Dark mode via `prefers-color-scheme`. SVG donut by category and a squarified treemap of the largest folders, with layout computed in F# and emitted as `<rect>` elements. All user-supplied strings (paths, rule names) HTML-escaped — a filename can contain `<`.

- [ ] **Step 1: Write the failing tests**

Create `tests/TestReports.fsx`:

```fsharp
open System
open TestFramework
open Types

/// Builds a minimal ScanResult for rendering tests.
let private sample () : Scanner.ScanResult =
    let meta path size =
        { Path = path; SizeBytes = size
          LastAccess = DateTime.UtcNow; LastModified = DateTime.UtcNow
          Created = DateTime.UtcNow; IsDirectory = false
          LinkTarget = None; Owner = "u"; Mode = "rw-r--r--" }
    { GeneratedAtUtc = DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc)
      Platform = MacOS
      ScanRoots = [ "/cache" ]
      Findings =
        [ { Meta = meta "/cache/a.tmp" 4096L; RuleName = "Cache"; Category = Caches
            Risk = Safe; Recoverability = Regenerated; Strategy = ContentsOnly
            Reason = "regenerated"; Candidate = None }
          { Meta = meta "/cache/<script>.tmp" 2048L; RuleName = "Cache"; Category = Caches
            Risk = Review; Recoverability = Unrecoverable; Strategy = MatchingFiles
            Reason = "needs review"; Candidate = None } ]
      Errors = []
      Elapsed = TimeSpan.FromSeconds 2.0
      Counters = Diagnostics.snapshot (Diagnostics.create ()) }

let run () =
    suite "Report.Markdown"

    test "renders a heading, totals, and a category table" (fun () ->
        let md = Report.Markdown.render (sample ())
        assertContains "# DiskCleanupAdvisor" md "heading"
        assertContains "| Category" md "category table"
        assertContains "Caches" md "category name")

    test "distinguishes reclaimable from pending review" (fun () ->
        let md = Report.Markdown.render (sample ())
        assertContains "Reclaimable" md "reclaimable figure"
        assertContains "Pending review" md "pending figure")

    suite "Report.Html"

    test "is a complete self-contained document" (fun () ->
        let html = Report.Html.render (sample ())
        assertContains "<!DOCTYPE html>" html "doctype"
        assertContains "</html>" html "closing tag"
        assertContains "<style>" html "inline styles")

    test "references no external resource" (fun () ->
        // A CDN reference would break the file offline and leak the scan
        // to a third party the moment it is opened.
        let html = Report.Html.render (sample ())
        assertFalse (html.Contains "http://") "no http references"
        assertFalse (html.Contains "https://") "no https references"
        assertFalse (html.Contains "<script src") "no external scripts")

    test "escapes HTML metacharacters in paths" (fun () ->
        // A filename can legitimately contain < or &.
        let html = Report.Html.render (sample ())
        assertFalse (html.Contains "/cache/<script>.tmp") "raw path not emitted"
        assertContains "&lt;script&gt;" html "path is escaped")

    test "supports dark mode" (fun () ->
        assertContains "prefers-color-scheme" (Report.Html.render (sample ())) "dark mode query")

    test "includes an SVG chart" (fun () ->
        let html = Report.Html.render (sample ())
        assertContains "<svg" html "svg present")
```

- [ ] **Step 2: Wire in, run, and confirm failure**

Modify `tests/Tests.fsx` per Canonical load order. Run `dotnet fsi tests/Tests.fsx` → FAIL, `Report.Markdown` not defined.

- [ ] **Step 3: Write Markdown.fsx**

```fsharp
(**
    Markdown report. Suitable for pasting into notes or committing to a repo.

    Renders from the same ScanResult as every other format; no format has
    access to data the others lack.
*)
module Report.Markdown

open System
open System.Text

/// Escapes the pipe character so a path cannot break out of a table cell.
let private cell (text: string) : string = text.Replace("|", "\\|")

/// Renders the full Markdown report.
let render (result: Scanner.ScanResult) : string =
    let sb = StringBuilder()
    let line (text: string) = sb.AppendLine text |> ignore

    line "# DiskCleanupAdvisor"
    line ""
    line (sprintf "Generated %s on %A." (result.GeneratedAtUtc.ToString "u") result.Platform)
    line ""
    line "## Space"
    line ""
    line (sprintf "- **Reclaimable:** %s — eligible for automatic removal"
              (Utilities.Format.bytes (Scanner.reclaimableBytes result)))
    line (sprintf "- **Pending review:** %s — requires your judgment"
              (Utilities.Format.bytes (Scanner.pendingReviewBytes result)))
    line ""
    line "Removal moves items to the Trash. Space is reclaimed when you empty it."
    line ""
    line "## By category"
    line ""
    line "| Category | Total | Reclaimable | Items |"
    line "|---|---:|---:|---:|"
    for category, findings in Scanner.byCategory result do
        let total = findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
        let claimable =
            findings |> List.filter (fun f -> f.Candidate.IsSome) |> List.sumBy (fun f -> f.Meta.SizeBytes)
        line (sprintf "| %s | %s | %s | %d |"
                  (cell (Types.Category.toText category))
                  (Utilities.Format.bytes total)
                  (Utilities.Format.bytes claimable)
                  findings.Length)
    line ""
    line "## Largest items"
    line ""
    line "| Risk | Size | Path | Rule |"
    line "|---|---:|---|---|"
    for f in result.Findings |> List.sortByDescending (fun f -> f.Meta.SizeBytes) |> List.truncate 50 do
        line (sprintf "| %s | %s | `%s` | %s |"
                  (Types.RiskLevel.toText f.Risk)
                  (Utilities.Format.bytes f.Meta.SizeBytes)
                  (cell f.Meta.Path)
                  (cell f.RuleName))
    line ""

    let unreadable = Scanner.unreadableDirectoryCount result
    if unreadable > 0 then
        line (sprintf "> %d directories were unreadable. Grant Full Disk Access to your terminal to include them." unreadable)
        line ""

    sb.ToString()

/// Writes the Markdown report to disk.
let write (path: string) (result: Scanner.ScanResult) : Result<unit, string> =
    try
        let directory = IO.Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (IO.Directory.Exists directory) then
            IO.Directory.CreateDirectory directory |> ignore
        IO.File.WriteAllText(path, render result)
        Ok ()
    with ex -> Error (sprintf "cannot write %s: %s" path ex.Message)
```

- [ ] **Step 4: Write Html.fsx**

```fsharp
(**
    Self-contained HTML report.

    Everything is inlined: styles, script, and chart geometry. The file opens
    correctly with no network access, which matters both for offline use and
    because a scan report lists the paths on someone's disk — it should not
    make a request to a third party when opened.

    All interpolated text is escaped. A filename may legitimately contain
    `<` or `&`.
*)
module Report.Html

open System
open System.Text

/// Escapes text for safe interpolation into HTML.
let private escape (text: string) : string =
    text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

/// A fixed palette, chosen to stay legible in both light and dark themes.
let private palette =
    [| "#4f8ef7"; "#f7934f"; "#4fd18b"; "#d14f8e"; "#a24ff7"
       "#f7d64f"; "#4fd1d1"; "#f76a4f"; "#8ef74f"; "#9e9e9e" |]

/// Renders an SVG donut chart of category shares.
let private donut (slices: (string * int64) list) : string =
    let total = slices |> List.sumBy snd
    if total = 0L then "<p>No data.</p>"
    else
        let sb = StringBuilder()
        sb.Append "<svg viewBox=\"0 0 42 42\" class=\"donut\" role=\"img\">" |> ignore
        // A circle of circumference 100 lets each slice be expressed directly
        // as a percentage via stroke-dasharray, with no trigonometry.
        let mutable offset = 25.0
        slices |> List.iteri (fun i (_, value) ->
            let percent = float value / float total * 100.0
            let color = palette.[i % palette.Length]
            sb.Append(
                sprintf "<circle cx=\"21\" cy=\"21\" r=\"15.915\" fill=\"transparent\" stroke=\"%s\" stroke-width=\"6\" stroke-dasharray=\"%.2f %.2f\" stroke-dashoffset=\"%.2f\"/>"
                    color percent (100.0 - percent) offset) |> ignore
            offset <- offset - percent)
        sb.Append "</svg>" |> ignore
        sb.ToString()

/// Renders a simple treemap of the largest items as proportional bars.
///
/// A full squarified treemap adds considerable geometry for little gain at
/// this scale, so rows are laid out proportionally by width — the comparison
/// a reader actually makes is between sizes, which this preserves.
let private treemap (items: (string * int64) list) : string =
    let total = items |> List.sumBy snd
    if total = 0L then ""
    else
        let sb = StringBuilder()
        sb.Append "<div class=\"treemap\">" |> ignore
        items |> List.iteri (fun i (label, value) ->
            let percent = float value / float total * 100.0
            let color = palette.[i % palette.Length]
            sb.Append(
                sprintf "<div class=\"tile\" style=\"width:%.2f%%;background:%s\" title=\"%s — %s\"><span>%s</span></div>"
                    (max percent 2.0) color (escape label) (Utilities.Format.bytes value) (escape label)) |> ignore)
        sb.Append "</div>" |> ignore
        sb.ToString()

/// Renders the complete HTML document.
let render (result: Scanner.ScanResult) : string =
    let grouped = Scanner.byCategory result
    let categoryTotals =
        grouped |> List.map (fun (c, fs) -> Types.Category.toText c, fs |> List.sumBy (fun f -> f.Meta.SizeBytes))
    let largest =
        result.Findings |> List.sortByDescending (fun f -> f.Meta.SizeBytes) |> List.truncate 200

    let rows =
        largest
        |> List.map (fun f ->
            sprintf "<tr data-risk=\"%s\"><td><span class=\"risk r-%s\">%s</span></td><td class=\"num\" data-bytes=\"%d\">%s</td><td class=\"path\">%s</td><td>%s</td></tr>"
                (Types.RiskLevel.toText f.Risk)
                ((Types.RiskLevel.toText f.Risk).ToLowerInvariant())
                (Types.RiskLevel.toText f.Risk)
                f.Meta.SizeBytes
                (Utilities.Format.bytes f.Meta.SizeBytes)
                (escape f.Meta.Path)
                (escape f.RuleName))
        |> String.concat "\n"

    let categoryRows =
        grouped
        |> List.map (fun (category, findings) ->
            let total = findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
            let claimable =
                findings |> List.filter (fun f -> f.Candidate.IsSome) |> List.sumBy (fun f -> f.Meta.SizeBytes)
            sprintf "<tr><td>%s</td><td class=\"num\">%s</td><td class=\"num\">%s</td><td class=\"num\">%d</td></tr>"
                (escape (Types.Category.toText category))
                (Utilities.Format.bytes total)
                (Utilities.Format.bytes claimable)
                findings.Length)
        |> String.concat "\n"

    let unreadableNote =
        let n = Scanner.unreadableDirectoryCount result
        if n = 0 then ""
        else sprintf "<p class=\"warn\">%d directories were unreadable. Grant Full Disk Access to your terminal to include them.</p>" n

    // Assembled as one string. No external resource is referenced anywhere.
    sprintf """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DiskCleanupAdvisor report</title>
<style>
:root { --bg:#ffffff; --fg:#1a1a1a; --muted:#666; --line:#e3e3e3; --card:#f7f7f8; }
@media (prefers-color-scheme: dark) {
  :root { --bg:#14161a; --fg:#e8e8ea; --muted:#9aa0a6; --line:#2a2d33; --card:#1b1e24; }
}
* { box-sizing:border-box; }
body { margin:0; padding:2rem 1.25rem; background:var(--bg); color:var(--fg);
       font:15px/1.55 ui-sans-serif,-apple-system,Segoe UI,Roboto,sans-serif; }
main { max-width:1100px; margin:0 auto; }
h1 { font-size:1.6rem; margin:0 0 .25rem; }
.sub { color:var(--muted); margin:0 0 2rem; }
.cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(220px,1fr)); gap:1rem; margin-bottom:2rem; }
.card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:1rem 1.1rem; }
.card .label { color:var(--muted); font-size:.82rem; text-transform:uppercase; letter-spacing:.04em; }
.card .value { font-size:1.5rem; font-weight:600; margin-top:.3rem; }
.donut { width:180px; height:180px; }
.treemap { display:flex; height:46px; border-radius:8px; overflow:hidden; margin:1rem 0 2rem; }
.tile { display:flex; align-items:center; justify-content:center; overflow:hidden; }
.tile span { font-size:.7rem; color:#fff; white-space:nowrap; padding:0 .3rem; }
table { width:100%%; border-collapse:collapse; margin-bottom:2rem; }
th, td { text-align:left; padding:.5rem .6rem; border-bottom:1px solid var(--line); }
th { color:var(--muted); font-size:.8rem; text-transform:uppercase; letter-spacing:.04em; cursor:pointer; user-select:none; }
td.num { text-align:right; font-variant-numeric:tabular-nums; }
td.path { font-family:ui-monospace,SFMono-Regular,Menlo,monospace; font-size:.82rem; word-break:break-all; }
.risk { font-size:.72rem; padding:.15rem .45rem; border-radius:4px; font-weight:600; }
.r-safe { background:#1f7a4d; color:#fff; }
.r-probablysafe { background:#2b6cb0; color:#fff; }
.r-review { background:#9a6a00; color:#fff; }
.r-dangerous { background:#8a2be2; color:#fff; }
.r-neverdelete { background:#a02020; color:#fff; }
#q { width:100%%; padding:.6rem .7rem; margin-bottom:1rem; border:1px solid var(--line);
     border-radius:8px; background:var(--card); color:var(--fg); font-size:.9rem; }
.warn { background:#9a6a0022; border-left:3px solid #9a6a00; padding:.7rem .9rem; border-radius:4px; }
details { margin-bottom:1.5rem; }
summary { cursor:pointer; font-weight:600; padding:.4rem 0; }
</style>
</head>
<body>
<main>
<h1>DiskCleanupAdvisor</h1>
<p class="sub">Generated %s &middot; %A &middot; %d items across %d roots</p>

<div class="cards">
  <div class="card"><div class="label">Reclaimable</div><div class="value">%s</div>
    <div class="sub" style="margin:0">eligible for automatic removal</div></div>
  <div class="card"><div class="label">Pending review</div><div class="value">%s</div>
    <div class="sub" style="margin:0">requires your judgment</div></div>
  <div class="card"><div class="label">Scan duration</div><div class="value">%s</div></div>
</div>

%s

<details open><summary>By category</summary>
%s
%s
<table>
<thead><tr><th>Category</th><th class="num">Total</th><th class="num">Reclaimable</th><th class="num">Items</th></tr></thead>
<tbody>
%s
</tbody></table>
</details>

<details open><summary>Largest items</summary>
<input id="q" type="search" placeholder="Filter by path or rule&hellip;" aria-label="Filter items">
<table id="items">
<thead><tr><th>Risk</th><th class="num" data-sort="num">Size</th><th>Path</th><th>Rule</th></tr></thead>
<tbody>
%s
</tbody></table>
</details>

<p class="sub">Removal moves items to the Trash. Space is reclaimed when you empty it.</p>
</main>
<script>
// Live filter over the item table.
const q = document.getElementById('q');
const rows = Array.from(document.querySelectorAll('#items tbody tr'));
q.addEventListener('input', () => {
  const needle = q.value.toLowerCase();
  for (const row of rows) {
    row.style.display = row.textContent.toLowerCase().includes(needle) ? '' : 'none';
  }
});
// Click a header to sort. Size sorts numerically via the data-bytes attribute.
document.querySelectorAll('#items th').forEach((th, index) => {
  let ascending = true;
  th.addEventListener('click', () => {
    const body = document.querySelector('#items tbody');
    const sorted = rows.slice().sort((a, b) => {
      const ca = a.children[index], cb = b.children[index];
      const na = ca.dataset.bytes, nb = cb.dataset.bytes;
      const result = (na !== undefined && nb !== undefined)
        ? Number(na) - Number(nb)
        : ca.textContent.localeCompare(cb.textContent);
      return ascending ? result : -result;
    });
    ascending = !ascending;
    for (const row of sorted) body.appendChild(row);
  });
});
</script>
</body>
</html>"""
        (result.GeneratedAtUtc.ToString "u")
        result.Platform
        result.Findings.Length
        result.ScanRoots.Length
        (Utilities.Format.bytes (Scanner.reclaimableBytes result))
        (Utilities.Format.bytes (Scanner.pendingReviewBytes result))
        (Utilities.Format.duration result.Elapsed)
        unreadableNote
        (donut categoryTotals)
        (treemap (largest |> List.truncate 12 |> List.map (fun f -> IO.Path.GetFileName f.Meta.Path, f.Meta.SizeBytes)))
        categoryRows
        rows

/// Writes the HTML report to disk.
let write (path: string) (result: Scanner.ScanResult) : Result<unit, string> =
    try
        let directory = IO.Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (IO.Directory.Exists directory) then
            IO.Directory.CreateDirectory directory |> ignore
        IO.File.WriteAllText(path, render result)
        Ok ()
    with ex -> Error (sprintf "cannot write %s: %s" path ex.Message)
```

Note the doubled `%%` in the CSS: `sprintf` treats `%` as a format specifier, so literal percent signs in `width:100%%` and `#q { width:100%% }` must be escaped. Missing one produces a runtime format exception, not a compile error.

- [ ] **Step 5: Wire both formats into the entry point**

In `DiskCleanupAdvisor.fsx`, add the two modules in Canonical load order and extend the report-writing block in `runScan`:

```fsharp
              match options.MarkdownPath with
              | Some path ->
                  match Report.Markdown.write path result with
                  | Ok () -> log |> Logging.info (sprintf "wrote Markdown report to %s" path)
                  | Error e -> yield e
              | None -> ()
              match options.HtmlPath with
              | Some path ->
                  match Report.Html.write path result with
                  | Ok () -> log |> Logging.info (sprintf "wrote HTML report to %s" path)
                  | Error e -> yield e
              | None -> ()
```

- [ ] **Step 6: Run the tests**

Run: `dotnet fsi tests/Tests.fsx` → PASS, 148 tests.

- [ ] **Step 7: Generate and inspect a real HTML report**

```bash
dotnet fsi DiskCleanupAdvisor.fsx -- --scan --html /tmp/report.html --markdown /tmp/report.md > /dev/null
grep -c "http://\|https://\|<script src" /tmp/report.html   # must print 0
open /tmp/report.html
```

Expected: the grep prints `0`, confirming self-containment. In the browser, verify the donut renders, the filter box narrows the table, clicking `Size` sorts numerically, and the page is legible in both light and dark system themes.

- [ ] **Step 8: Commit**

```bash
git add src/Report/Markdown.fsx src/Report/Html.fsx tests/TestReports.fsx tests/Tests.fsx DiskCleanupAdvisor.fsx
git commit -m "feat: add Markdown and self-contained HTML reports

HTML inlines all styles, script, and chart geometry — no external
resource is referenced, so the report opens offline and does not
disclose scanned paths to a third party. All interpolated text is
escaped, since a filename may contain < or &."
```

---

### Task 17: Time Machine snapshots, parallel traversal, and polish

**Files:**
- Create: `src/Snapshots.fsx`
- Modify: `src/DirectoryWalker.fsx`, `src/Report/Console.fsx`, `DiskCleanupAdvisor.fsx`, `README.md`

**Interfaces:**
- Produces:
  - `Snapshots.LocalSnapshot` = `{ Name: string; CreatedUtc: DateTime option }`
  - `Snapshots.list : unit -> Result<LocalSnapshot list, string>`
  - `Snapshots.renderAdvice : LocalSnapshot list -> string`

**Why snapshots matter.** On macOS, Time Machine local snapshots are frequently the largest single consumer of apparent disk space, and **no amount of file deletion reclaims them**. A user who runs this tool, frees 3 GB of caches, and sees no change in available space has been misled. Confirmed present on the development machine via `tmutil listlocalsnapshots /`.

The tool **reports** snapshots and explains the `tmutil deletelocalsnapshots` command. It does not run it — that is a system-level operation outside this tool's safety model.

- [ ] **Step 1: Write Snapshots.fsx**

```fsharp
(**
    Time Machine local snapshot reporting (macOS only).

    Local snapshots frequently consume more space than everything this tool can
    remove combined, and no file deletion reclaims them. Reporting caches
    without mentioning snapshots would leave a user confused about why their
    free space did not change.

    This module only reports. Removing a snapshot is a system-level operation
    with its own consequences, so the tool prints the command and lets the user
    decide.
*)
module Snapshots

open System
open System.Diagnostics

/// One local Time Machine snapshot.
type LocalSnapshot =
    { Name: string
      CreatedUtc: DateTime option }

/// Parses the timestamp embedded in a snapshot name such as
/// `com.apple.TimeMachine.2026-07-20-110340.local`.
let private parseTimestamp (name: string) : DateTime option =
    let parts = name.Split '.'
    parts
    |> Array.tryFind (fun p -> p.Length = 17 && p.[4] = '-')
    |> Option.bind (fun stamp ->
        match DateTime.TryParseExact(
                  stamp, "yyyy-MM-dd-HHmmss",
                  Globalization.CultureInfo.InvariantCulture,
                  Globalization.DateTimeStyles.AssumeLocal) with
        | true, value -> Some (value.ToUniversalTime())
        | _ -> None)

/// Lists local snapshots on the boot volume.
/// Returns an empty list on non-macOS platforms rather than an error.
let list () : Result<LocalSnapshot list, string> =
    if Platform.current () <> Types.MacOS then Ok []
    else
        try
            let startInfo =
                ProcessStartInfo(
                    FileName = "tmutil",
                    Arguments = "listlocalsnapshots /",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false)
            use process' = Process.Start startInfo
            let output = process'.StandardOutput.ReadToEnd()
            process'.WaitForExit()
            if process'.ExitCode <> 0 then Ok []      // not an error worth failing a scan over
            else
                output.Split '\n'
                |> Array.map (fun line -> line.Trim())
                |> Array.filter (fun line -> line.StartsWith "com.apple.TimeMachine")
                |> Array.map (fun name -> { Name = name; CreatedUtc = parseTimestamp name })
                |> Array.toList
                |> Ok
        with ex -> Error (sprintf "cannot list local snapshots: %s" ex.Message)

/// Renders advice about local snapshots, or an empty string when there are none.
let renderAdvice (snapshots: LocalSnapshot list) : string =
    if List.isEmpty snapshots then ""
    else
        let oldest =
            snapshots
            |> List.choose (fun s -> s.CreatedUtc)
            |> function [] -> None | dates -> Some (List.min dates)
        let age =
            match oldest with
            | Some date -> sprintf ", oldest from %s" (date.ToLocalTime().ToString "yyyy-MM-dd")
            | None -> ""
        String.concat "\n"
            [ sprintf "  %d Time Machine local snapshot(s) present%s." snapshots.Length age
              "  These can consume more space than everything listed above, and"
              "  deleting files does not reclaim them. To inspect and remove:"
              ""
              "      tmutil listlocalsnapshots /"
              "      tmutil deletelocalsnapshots <date>"
              ""
              "  This tool does not run those commands for you." ]
```

- [ ] **Step 2: Surface snapshots in the console report**

In `src/Report/Console.fsx`, add a parameter and render the advice. Change the signature to `render (snapshots: Snapshots.LocalSnapshot list) (result: Scanner.ScanResult) : string` and insert before the dry-run notice:

```fsharp
    let snapshotAdvice = Snapshots.renderAdvice snapshots
    if snapshotAdvice <> "" then
        append (colorize bold "Time Machine")
        append snapshotAdvice
        append ""
```

Update both call sites in `DiskCleanupAdvisor.fsx` to pass the snapshot list, obtained once per run:

```fsharp
    let snapshots =
        match Snapshots.list () with
        | Ok found -> found
        | Error message ->
            log |> Logging.warn message
            []
```

- [ ] **Step 3: Verify against the real machine**

Run: `dotnet fsi DiskCleanupAdvisor.fsx -- --scan`

Expected: a **Time Machine** section listing the snapshot count. The development machine is known to have at least one, so an empty section here means the parsing is wrong. Cross-check with `tmutil listlocalsnapshots / | grep -c com.apple`.

- [ ] **Step 4: Add parallel traversal, but only if it helps**

Measure the sequential walk first:

```bash
time dotnet fsi DiskCleanupAdvisor.fsx -- --scan > /dev/null
```

If the scan completes in under about 10 seconds, **stop — do not add parallelism.** A correct sequential walker is worth more than a marginally faster parallel one, and the complexity is only justified by a measured problem.

If it is slower, replace the `while pending.Count > 0` loop in `src/DirectoryWalker.fsx` with a bounded-parallel version, keeping the same signature so nothing downstream changes:

```fsharp
        // Bounded parallelism. This workload is I/O-bound, so oversubscribing
        // a single volume slows it down; the cap stays low deliberately.
        let queue = System.Collections.Concurrent.ConcurrentQueue<string * int>()
        queue.Enqueue(root, 0)
        let files = System.Collections.Concurrent.ConcurrentBag<FileMeta>()
        let errors = System.Collections.Concurrent.ConcurrentBag<FsError>()
        let options' = System.Threading.Tasks.ParallelOptions(MaxDegreeOfParallelism = options.ThreadCount)

        let rec drain () =
            let batch = ResizeArray<string * int>()
            let mutable item = Unchecked.defaultof<string * int>
            while queue.TryDequeue &item do batch.Add item
            if batch.Count > 0 then
                System.Threading.Tasks.Parallel.ForEach(batch, options', fun (directory, depth) ->
                    // identical per-directory body as the sequential version,
                    // enqueueing subdirectories instead of pushing to a stack
                    processDirectory queue files errors directory depth) |> ignore
                drain ()
        drain ()
```

Re-run the full test suite afterward: `dotnet fsi tests/Tests.fsx`. All 148 tests must still pass — the walker tests are the regression guard for this change.

- [ ] **Step 5: Write the README**

Create `README.md` covering: what the tool does, the trash-only guarantee, installation (`dotnet fsi DiskCleanupAdvisor.fsx -- --help`), every command-line flag, the configuration file format with a worked example, the Full Disk Access requirement on macOS, the three-gate safety model, and how to add a rule (append a record to `src/Rules.fsx`; the scanner needs no change).

State plainly in the opening section: **this tool moves files to the Trash and never deletes them; you reclaim space by emptying the Trash yourself.**

- [ ] **Step 6: Final verification**

```bash
dotnet fsi tests/Tests.fsx                       # all tests pass
grep -rn "File.Delete\|Directory.Delete" src/    # must find nothing
dotnet fsi DiskCleanupAdvisor.fsx -- --help      # usage text
dotnet fsi DiskCleanupAdvisor.fsx -- --scan      # full report
echo "no" | dotnet fsi DiskCleanupAdvisor.fsx -- --delete   # cancels safely
```

- [ ] **Step 7: Commit**

```bash
git add src/Snapshots.fsx src/Report/Console.fsx DiskCleanupAdvisor.fsx README.md tests/Tests.fsx
git commit -m "feat: report Time Machine snapshots and add README

Local snapshots often consume more space than everything this tool can
remove, and no file deletion reclaims them. Reporting caches without
mentioning them would leave a user confused about why free space did
not change. The tool reports and prints the tmutil command; it does
not run it."
```

---

## Spec coverage

Every requirement in the design spec maps to a task.

| Spec section | Task |
|---|---|
| Module graph | 1–17 (one module per task group) |
| Core types | 1 |
| Three-gate safety model | 8 |
| Denylist veto, override clamping | 8 |
| Private `DeletionCandidate` constructor | 8 |
| Rule engine, macOS catalog, Linux/Windows seeds | 7 |
| `~/Library/Containers` narrow-then-broad precedence | 7 (catalog), 12 (first-match) |
| Traversal, permission collection, symlink refusal | 11 |
| Full Disk Access reporting | 11, 13 |
| Duplicate funnel | 14 |
| Trash-only cleaner, journal, re-stat | 15 |
| Console / JSON / Markdown / HTML reports | 13, 16 |
| Reclaimable vs pending-review separation | 12, 13, 16 |
| Configuration with accumulating validation | 6 |
| CLI, dry-run default | 13 |
| Diagnostics | 4 |
| Testability via `FileSystemOps` | 5 |
| Time Machine snapshots (open risk in spec) | 17 |
| `node_modules` aggregate reporting | 7 |
| `/Applications` report-only | 7, 8 |

## Deferred deliberately

- **Parallel traversal** — added in Task 17 only if measurement justifies it.
- **Windows Recycle Bin** — `--delete` refuses on Windows rather than falling back to an irreversible unlink. Requires `SHFileOperation` P/Invoke.
- **Squarified treemap** — Task 16 ships proportional bars; true squarification adds geometry for little gain at this scale.
- **Unused-application detection** — `/Applications` is report-only by design; ranking by last-used date needs `mdls` and is not in v1.

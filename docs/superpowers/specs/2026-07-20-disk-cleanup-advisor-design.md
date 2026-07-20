# DiskCleanupAdvisor — Design

**Date:** 2026-07-20
**Status:** Approved
**Source spec:** [docs/MacDisk-Deleter-plan.md](../../MacDisk-Deleter-plan.md)

## Purpose

A cross-platform F# script that finds disposable files on a developer machine, explains
why each is disposable, and — only on explicit request — moves them to the platform Trash.
The immediate goal is reclaiming space on a macOS development machine; Linux and Windows
share the same engine with a smaller rule catalog.

The tool's defining constraint: **it must be impossible to lose data by using it.**
Every design decision below is subordinate to that.

## Decisions made during design

| Question | Decision | Rationale |
|---|---|---|
| Single `.fsx` vs. multiple files | Multiple `.fsx` modules composed by one entry script via `#load` | Keeps every file within the 200–400 line guideline, allows a test script to load the same modules, and still requires no build step or NuGet restore |
| Delete semantics | Move to Trash only; no `unlink` path exists in the codebase | Makes every operation reversible by the user. Space is not reclaimed until the user empties Trash, which the reports state explicitly |
| Platform scope for v1 | macOS rule catalog complete; Linux and Windows catalogs seeded | Destructive rules that cannot be tested on the available machine should not ship |
| Report formats | Console, Markdown, HTML, JSON — all four | JSON doubles as the test fixture format; HTML is the surface for actually reviewing candidates |

## Non-goals

- No GUI. Console only.
- No third-party packages. Everything comes from the .NET 10 base class library.
- No background daemon, scheduling, or watch mode.
- No permanent deletion. If a user wants space back immediately, they empty Trash themselves.
- No system-wide scanning as root. The tool targets the invoking user's home directory
  and well-known machine-wide cache locations that do not require elevation.

## Target

- F# 9, .NET 10 (`dotnet --version` on the dev machine: 10.0.302)
- Entry point: `dotnet fsi DiskCleanupAdvisor.fsx -- --scan`
- Platform detection via `System.Runtime.InteropServices.RuntimeInformation`

---

## 1. Module graph

`#load` directives are processed linearly, so file order must be a topologically sorted
dependency DAG. Each module carries a module-level doc comment and every public function
carries XML documentation.

| File | Responsibility | Depends on |
|---|---|---|
| `src/Types.fsx` | All DUs and records. No logic. | — |
| `src/Utilities.fsx` | Byte formatting, path normalization, `Result` combinators, glob matching | Types |
| `src/Logging.fsx` | Leveled structured logger, writes to stderr | Types, Utilities |
| `src/Diagnostics.fsx` | Counters, `Stopwatch`, throughput meters, memory sampling | Types, Utilities |
| `src/Platform.fsx` | OS detection, known-folder resolution, root/elevation check | Types, Utilities |
| `src/Configuration.fsx` | JSON config load, merge over defaults, validation | Types, Utilities, Platform |
| `src/Rules.fsx` | The rule catalog: macOS (full), Linux/Windows (seeded) | Types, Platform |
| `src/RiskEngine.fsx` | Gate evaluation, denylist veto, override clamping | Types, Utilities, Rules |
| `src/FileClassifier.fsx` | Extension and name patterns → `Category` | Types, Utilities |
| `src/Hashing.fsx` | SHA-256, MD5, sampled fast-hash | Types, Utilities |
| `src/DirectoryWalker.fsx` | Bounded-parallel traversal, error collection | Types, Utilities, Logging, Diagnostics |
| `src/Scanner.fsx` | Walker + rules + classifier → `ScanResult` | all above |
| `src/Duplicates.fsx` | Three-stage duplicate funnel | Types, Hashing, Utilities |
| `src/Cleaner.fsx` | Trash relocation per platform, deletion manifest | Types, Platform, RiskEngine, Logging |
| `src/Report/Json.fsx` | `System.Text.Json` serialization of `ScanResult` | Types, Utilities |
| `src/Report/Markdown.fsx` | Markdown rendering | Types, Utilities |
| `src/Report/Html.fsx` | Self-contained HTML with inline SVG charts | Types, Utilities, Report/Json |
| `src/Report/Console.fsx` | ANSI-colored console rendering | Types, Utilities |
| `src/Cli.fsx` | Argument parsing → `Result<Options, string list>` | Types, Utilities |
| `DiskCleanupAdvisor.fsx` | Composition root, ~120 lines | all |
| `tests/Tests.fsx` | `#load`s the same modules, asserts | all |

Target size: ~3,500 lines total. This is several implementation sessions, not one.

---

## 2. Core types

```fsharp
type Platform = MacOS | Linux | Windows

type RiskLevel =
    | Safe          // regenerable, no user data, rule-asserted
    | ProbablySafe  // regenerable but app may need to re-download
    | Review        // requires human judgment
    | Dangerous     // deletion likely breaks something
    | NeverDelete   // veto; no flag bypasses this

type Recoverability =
    | Regenerated       // the tool that created it will recreate it
    | Redownloadable    // recoverable from a network source
    | RecoverableFromTrash
    | Unrecoverable

type DeleteStrategy =
    | ContentsOnly    // empty the directory, keep the directory itself
    | MatchingFiles   // only files matching Include globs
    | WholeDirectory  // the directory itself is regenerable

type PathSpec =
    | HomeRelative of string
    | Absolute of string
    | EnvRelative of variable: string * relative: string
```

`RiskLevel` carries presentation metadata (color, console icon, description) through a
`RiskLevel.describe` function rather than fields on the DU, keeping the type comparable
and serializable.

---

## 3. The safety model

Nothing is moved unless **all three gates pass**. Gates are evaluated in order; each can
only reject, never approve on its own.

### Gate 1 — Rule assertion

A matched rule must carry `SafeToDelete = true` and `Risk = Safe`. Absent a matching rule,
a path is never a deletion candidate. The default answer is no.

### Gate 2 — Allowlist anchoring

The target must be a proper descendant of a rule's *resolved* root. The `Cleaner` module's
public function does not accept a bare path — it accepts a `DeletionCandidate` value that
can only be constructed by the `Scanner` from a rule match:

```fsharp
type DeletionCandidate = private {
    Path: string
    OriginRule: Rule
    ResolvedRoot: string
    SizeBytes: int64
}
```

Because the constructor is private to the scanner's module, "delete an arbitrary path" is
structurally unreachable rather than defended against at runtime.

### Gate 3 — Denylist veto

A hardcoded set evaluated last, which overrides everything:

```
~/Documents          ~/Desktop           ~/Pictures
~/Movies             ~/Music             ~/Downloads
/System              /Library            /Applications
~/Library/Application Support   (except explicitly enumerated cache subpaths)
any path segment equal to .git, or any path with a .git ancestor
any path that is, or is inside, the script's own directory
```

`/Applications` appearing in the veto set means installed applications are **reported but
never deletable by this tool**. The "unused applications" scan category exists — it ranks
apps by size and last-used date so the user can uninstall them deliberately — but the
tool will not move an application bundle to Trash. Uninstalling frequently requires
removing associated support files and daemons that this tool has no knowledge of.

**The veto outranks `--force`, outranks config `RiskOverrides`, and has no bypass flag.**
Config overrides are clamped: they may only *raise* risk (`Safe → Review`), never lower it.
A config attempting to lower risk is a validation error, reported and rejected at load time.

### Additional invariants

- Symlinks and reparse points are never followed (`FileSystemInfo.LinkTarget` is non-null → skip and record).
- Traversal never crosses a filesystem boundary (device ID compared against the scan root).
- The script refuses to run as root/Administrator unless `--allow-root` is passed.
- Deletion is preceded by a re-`stat`: if the file changed since the scan, it is skipped.

---

## 4. Rule engine

```fsharp
type Rule = {
    Name: string
    Platforms: Set<Platform>
    Root: PathSpec
    Include: string list          // globs, relative to Root
    Exclude: string list
    Risk: RiskLevel
    Recoverability: Recoverability
    Strategy: DeleteStrategy
    MinAge: TimeSpan option
    MinSize: int64 option
    SafeToDelete: bool
    Description: string
    Reason: string
    EstimatedBenefit: string
    Examples: string list
}
```

The catalog is a plain `Rule list`. The scanner is parameterized over that list and reads
no rule-specific logic, so **adding a rule means appending a record — the scanner never
changes.** Rules whose `Root` does not resolve on the current machine are dropped during
startup with a debug-level log line.

`DeleteStrategy` matters in practice: Xcode's `DerivedData` must be emptied but must keep
existing, because Xcode expects the directory. A stale `.dmg` in `~/Downloads` is a
`MatchingFiles` candidate. Getting this wrong breaks tools rather than losing data, but it
still breaks tools.

### Rule precedence

Rules are matched most-specific-first by resolved root path length, then by declaration
order. This is what makes the `~/Library/Containers` requirement expressible as two rules:

1. `Containers/*/Data/Library/Caches` and `Containers/*/Data/tmp` → `Safe`
2. `Containers/**` → `Review` (catch-all, lower precedence)

### macOS catalog (v1, complete)

`~/Library/Caches`, `~/Library/Logs`, `~/Library/Application Support/CrashReporter`,
`~/Library/Developer/Xcode/DerivedData`, `~/Library/Developer/Xcode/Archives`,
`~/Library/Developer/Xcode/iOS DeviceSupport`, `~/Library/Developer/CoreSimulator/Caches`,
`~/.Trash` (report only — reclaim requires emptying), browser caches (Safari, Chrome,
Firefox, Edge, Arc), `~/.nuget/packages`, `~/Library/Caches/Homebrew`,
`~/.npm/_cacache`, `~/Library/pnpm/store`, `~/.yarn/cache`, `~/.cache/pip`,
`~/.gradle/caches`, `~/.m2/repository`, Docker Desktop data, Podman machine images,
`~/.terraform.d/plugin-cache`, `__pycache__`, `node_modules` (Review, never Safe),
plus the cross-platform categories: large files, duplicates, old archives, ISO/DMG
installers, empty directories.

### Linux / Windows catalogs (v1, seeded)

Linux: `/tmp`, `/var/tmp`, `~/.cache`, apt/dnf/pacman caches, `journalctl` logs, snap
revisions, flatpak unused runtimes.
Windows: `%TEMP%`, `Prefetch`, `SoftwareDistribution\Download`, NuGet, Visual Studio and
Rider caches, Recycle Bin.

These use the identical engine and are populated incrementally as they can be verified on
a real machine of that platform.

---

## 5. Traversal

`DirectoryWalker` uses a `System.Threading.Channels.Channel<string>` producer feeding
`Parallel.ForEachAsync` with bounded degree of parallelism. Default DOP is
`min(Environment.ProcessorCount, 8)` — this workload is I/O-bound, and oversubscribing a
single APFS volume measurably slows it down. `--threads` overrides.

Every filesystem call returns `Result<'T, FsError>`. Permission denials are **collected,
not thrown**. On macOS this is the common case, not an edge case: TCC denies
`~/Library/Containers`, `~/Library/Mail`, `~/Library/Messages`, and others unless the
invoking terminal has Full Disk Access. The walker counts denials by directory, and the
console report closes with an actionable note:

> 47 directories were unreadable. Grant Full Disk Access to your terminal in
> System Settings → Privacy & Security to include them.

Per-file metadata collected: size, last access, last modified, creation time, owner UID,
mode bits, and whether the entry is a link. Depth is capped (default 32) to bound
pathological trees.

---

## 6. Duplicate detection

Hashing a home directory naively means reading hundreds of gigabytes. A three-stage funnel
avoids that:

1. **Group by exact byte size.** `stat` only, no reads. Singleton groups drop out
   immediately, which eliminates roughly 95% of candidates on a typical machine.
2. **Sampled fast-hash** on surviving groups: SHA-256 over
   `first 64 KB ‖ last 64 KB ‖ size`. Two reads per file regardless of file size.
3. **Full streaming SHA-256** only where stage 2 still collides.

Floor at `--min-size` (default 1 MB) — duplicate 4 KB files are not worth the I/O.

**Duplicates are classified `Review`, never `Safe`.** Identical bytes do not imply a copy
is disposable; the second copy may be the one a running program references. The report
retains the shallowest, oldest path as the keeper and lists the rest as candidates.
Duplicates are excluded from `--delete` entirely unless `--force` is passed, and even then
they go to Trash like everything else.

`--hash-algorithm` selects SHA-256 (default) or MD5 for stage 3. MD5 is offered for speed
on trusted local data only; the help text says so.

---

## 7. Cleaner

Platform-specific Trash relocation, all in-box:

- **macOS** — move into `~/.Trash`, with `Put Back` metadata written where the volume
  supports it. Name collisions resolved by appending a timestamp.
- **Linux** — XDG Trash spec: move into `~/.local/share/Trash/files/`, write the
  corresponding `.trashinfo` file into `~/.local/share/Trash/info/`.
- **Windows** — Recycle Bin via `SHFileOperation` P/Invoke with `FOF_ALLOWUNDO`.

Cross-device moves fall back to copy-then-delete-source, with the source removed only after
the copy is verified by size and hash.

Every deletion is journaled to `reports/deletions-<timestamp>.json` **before** the move:
path, size, hash, rule name, timestamp, destination. The journal is written and flushed
first so a crash mid-run leaves an accurate record.

`--delete` alone prompts for confirmation and processes only `Risk = Safe`. `--force`
additionally permits `ProbablySafe`. Nothing permits `Review`, `Dangerous`, or
`NeverDelete`.

---

## 8. Reports

All four formats render from the same `ScanResult` value; no format has privileged access
to data the others lack.

- **Console** (always on) — ANSI color, risk icons, grouped by category, sorted by
  reclaimable size descending. Degrades to plain text when stdout is redirected or
  `NO_COLOR` is set.
- **JSON** — full `ScanResult` via `System.Text.Json`. Doubles as the test fixture format
  and enables diffing two runs.
- **Markdown** — tables per category, suitable for pasting into notes.
- **HTML** — single self-contained file. Inline CSS and JS, no CDN, no external assets.
  Dark mode via `prefers-color-scheme` plus an explicit toggle. Client-side search, sort,
  and filter over an embedded JSON payload. SVG donut chart by category and a squarified
  treemap of largest folders, with layout computed in F# and emitted as `<rect>` elements.
  Collapsible sections per category. Progress bars showing each category's share of total
  reclaimable space.

Because deletion is trash-only, every report states two distinct figures:

- **Moved to Trash: X GB** — empty Trash to actually reclaim
- **Pending review: Y GB** — candidates requiring human judgment

Conflating these would let a user believe space was freed when it was not.

---

## 9. Configuration

JSON, located via `--config`, else `~/.config/diskcleanupadvisor/config.json`, else defaults.

```json
{
  "ignorePaths": ["~/Projects/active"],
  "ignoreExtensions": [".psd", ".sketch"],
  "minSizeBytes": 1048576,
  "ageThresholdDays": 30,
  "threadCount": 8,
  "hashAlgorithm": "sha256",
  "riskOverrides": { "Homebrew Cache": "Review" }
}
```

Validation runs at load time and returns `Result<Config, string list>` — all errors at
once, not first-failure. A `riskOverride` that lowers risk is rejected with an explanatory
message naming the rule.

---

## 10. CLI

Hand-rolled parser producing `Result<Options, string list>`. No dependency needed for this
surface area.

```
--scan               run a scan (default when no mode flag given)
--delete             move Safe-rated candidates to Trash, after confirmation
--force              additionally permit ProbablySafe; never bypasses the veto
--dry-run            explicit no-op mode (this is already the default)
--json <path>        write JSON report
--markdown <path>    write Markdown report
--html <path>        write HTML report
--duplicates         enable duplicate detection
--large-files        enable large-file reporting
--age <days>         only consider items older than N days
--min-size <bytes>   size floor for hashing and large-file reporting
--threads <n>        traversal parallelism
--hash-algorithm     sha256 | md5
--config <path>      configuration file
--allow-root         permit running elevated
--verbose            debug-level logging to stderr
--help
```

**Dry run is the default and requires no flag.** Running the script with no arguments
scans and reports; it cannot modify anything.

---

## 11. Testability

Filesystem access is a record of functions rather than an interface, keeping the code
functional and the substitution trivial:

```fsharp
type FileSystemOps = {
    EnumerateDirectories : string -> Result<string[], FsError>
    EnumerateFiles       : string -> Result<string[], FsError>
    GetInfo              : string -> Result<FileMeta, FsError>
    OpenRead             : string -> Result<Stream, FsError>
    MoveToTrash          : string -> string -> Result<unit, FsError>
}
```

`FileSystem.real` and `FileSystem.inMemory : TreeSpec -> FileSystemOps`. Tests live in
`tests/Tests.fsx`, `#load` the same modules, and run with `dotnet fsi tests/Tests.fsx`.

Coverage concentrates on the pure, dangerous logic rather than chasing a uniform percentage:

- Gate evaluation, including every rejection path
- Denylist precedence over `--force` and over config overrides
- Override clamping (a lowering override must be rejected)
- Glob matching, including `**` semantics
- Duplicate funnel grouping and keeper selection
- Byte and duration formatting

The gate logic additionally gets adversarial tests: path traversal via `../`, symlink
escape out of an allowlisted root, case-insensitive APFS collisions (`Caches` vs `caches`),
and Unicode normalization (macOS stores NFD; user config will contain NFC — a path
comparison that ignores this is a real correctness bug).

---

## 12. Diagnostics

Reported at completion, and every 2 seconds during traversal at `--verbose`:

elapsed time, peak managed memory, directories/second, files/second, bytes/second,
directories skipped by reason (permission, symlink, filesystem boundary, depth cap),
and total entries examined.

---

## 13. Execution flow

```
parse args                       → Result<Options, string list>
load and validate config         → Result<Config, string list>
detect platform, check elevation
resolve rule roots               (drop rules whose root does not exist)
scan: parallel walk → classify → match rules → RiskEngine
optional: duplicate funnel, large-file pass
emit reports                     (console always; md/html/json on flag)
if --delete:
    filter to Safe (or Safe+ProbablySafe with --force)
    apply three gates
    prompt for confirmation, showing count and total size
    journal, then move to Trash, then verify
report diagnostics
```

---

## 14. Implementation order

Each step ends with a working, runnable script.

1. `Types`, `Utilities`, `Logging` + tests for formatting and glob matching
2. `Platform`, `Configuration` + validation tests
3. `Rules` (macOS catalog), `RiskEngine` + the full adversarial gate test suite
4. `DirectoryWalker`, `FileClassifier`, `Scanner` + in-memory tree tests
5. `Report/Console`, `Report/Json` + `Cli` + entry point — **first end-to-end usable version**
6. `Report/Markdown`, `Report/Html`
7. `Hashing`, `Duplicates`
8. `Cleaner` + `--delete` path — last, and only once the gate suite is green
9. `Diagnostics` polish, Linux/Windows rule seeding

`Cleaner` is deliberately last. Until it exists, the tool is incapable of modifying
anything, which makes every earlier step safe to run against a real home directory.

---

## Open risks

- **Full Disk Access.** Without it, scan coverage on macOS is materially incomplete. The
  tool detects and reports this rather than silently under-reporting.
- **`node_modules` volume.** These directories contain enormous file counts and will
  dominate traversal time. They are classified `Review` (never `Safe`) and the walker
  reports them as a single aggregate rather than enumerating every file.
- **Time Machine local snapshots.** Frequently the largest consumer of apparent disk space
  on macOS and not addressable by file deletion at all. v1 detects and reports snapshot
  space via `tmutil listlocalsnapshots` and tells the user it must be handled separately —
  it does not attempt to remove them.

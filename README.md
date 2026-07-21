# DiskCleanupAdvisor

DiskCleanupAdvisor scans a macOS, Linux, or Windows machine for disposable
files — caches, logs, build artifacts, crash reports, duplicates — and
explains, item by item, why each one is (or is not) safe to remove. It never
deletes anything outright.

> **This tool moves files to the Trash and never deletes them. You reclaim
> space by emptying the Trash yourself.**

A dry-run scan is the default: running the tool with no arguments only
reports. Nothing is modified unless you pass `--delete` and then type `yes`
at an explicit confirmation prompt.

## Requirements

- .NET 10 SDK (for `dotnet fsi`)
- macOS, Linux, or Windows

There is no build step and no NuGet dependency. The tool is a tree of F#
script files (`.fsx`) run directly with `dotnet fsi`.

## Installation

Nothing to install — clone the repository and run the entry point in place:

```bash
dotnet fsi DiskCleanupAdvisor.fsx -- --help
```

Everything after `--` is passed to the tool; `dotnet fsi` treats anything
before it as its own arguments.

## Usage

```bash
# Dry-run scan (default). Reports only; nothing is modified.
dotnet fsi DiskCleanupAdvisor.fsx -- --scan

# Write additional report formats alongside the console report.
dotnet fsi DiskCleanupAdvisor.fsx -- --scan --json out.json --markdown out.md --html out.html

# Review, then interactively move Safe-rated items to the Trash.
dotnet fsi DiskCleanupAdvisor.fsx -- --delete
```

`--delete` prints the same report as `--scan`, lists exactly what it intends
to move, asks for a typed `yes`, writes a journal of the operation, then
moves items to the platform Trash (never a raw unlink). It reports how many
items moved and reminds you that the space is not yet reclaimed until you
empty the Trash.

## Command-line flags

| Flag | Meaning |
|---|---|
| `--scan` | Scan and report. This is the default. |
| `--delete` | Move Safe-rated items to Trash, after confirmation. |
| `--dry-run` | Force scan mode. Overrides `--delete` regardless of argument order. |
| `--help` | Show usage text. |
| `--json <path>` | Write a JSON report. |
| `--markdown <path>` | Write a Markdown report. |
| `--html <path>` | Write a self-contained HTML report (inline CSS/JS/SVG, no network access). |
| `--duplicates` | Detect duplicate files by content hash. |
| `--large-files` | Report the largest files found. |
| `--age <days>` | Only consider items older than this many days. |
| `--min-size <bytes>` | Size floor for hashing and large-file reporting. |
| `--threads <n>` | Traversal parallelism (currently reserved; the walker is sequential — see below). |
| `--hash-algorithm <alg>` | `sha256` (default) or `md5`, used for duplicate detection. |
| `--config <path>` | Path to a configuration file. Defaults to `~/.config/diskcleanupadvisor/config.json`. |
| `--force` | Also permit `ProbablySafe` items. Never bypasses the denylist veto. |
| `--allow-root` | Permit running with elevated privileges (refused by default). |
| `--verbose` / `-v` | Debug-level logging to stderr. |

Nothing is deleted without `--delete` and an interactive `yes` typed at the
confirmation prompt.

## Configuration file

Settings load from `~/.config/diskcleanupadvisor/config.json` by default (or
the path given to `--config`). Every field is optional; anything omitted
falls back to the built-in default, and command-line flags override whatever
the file specifies. Validation accumulates every error at once, so a file
with three mistakes is fixed in one pass rather than three runs.

```json
{
  "ignorePaths": ["/Users/me/Projects/keep-this-cache"],
  "ignoreExtensions": [".psd"],
  "minSizeBytes": 1048576,
  "ageThresholdDays": 30,
  "threadCount": 8,
  "hashAlgorithm": "sha256",
  "riskOverrides": {
    "Homebrew cache": "Review"
  }
}
```

| Field | Type | Meaning |
|---|---|---|
| `ignorePaths` | string array | Absolute paths excluded from scanning entirely. |
| `ignoreExtensions` | string array | Lowercase extensions (with leading dot) excluded from results. |
| `minSizeBytes` | integer ≥ 0 | Files below this size are ignored for hashing and large-file reporting. |
| `ageThresholdDays` | integer ≥ 0 | Items younger than this are not considered stale. |
| `threadCount` | integer, 1–64 | Traversal parallelism setting. |
| `hashAlgorithm` | `"sha256"` or `"md5"` | Algorithm used for duplicate detection. |
| `riskOverrides` | object of rule name → risk level | Raises a named rule's risk level (e.g. to require manual review). Lowering a rule's declared risk is rejected — that direction is not permitted, since it would make an already-reviewed rule less safe silently. |

## macOS Full Disk Access

On macOS, TCC (Transparency, Consent, and Control) blocks read access to
several locations — `~/Library/Containers`, `~/Library/Mail`, and others —
unless the terminal application running the tool has been granted **Full
Disk Access**. Without it, those subtrees are skipped and counted as
unreadable directories; the console report states the count and points you
to **System Settings → Privacy & Security → Full Disk Access** to grant it
to your terminal. The scan still completes, just without visibility into
those locations.

## The three-gate safety model

A file only becomes an automatic-deletion candidate if it clears three
independent gates, evaluated in order:

1. **Rule assertion.** The matching rule must be rated `Safe` and explicitly
   set `SafeToDelete = true`. `Review`, `Dangerous`, and `NeverDelete` rules
   never produce a candidate regardless of any flag. `ProbablySafe` rules are
   rejected by default and only considered with `--force`.
2. **Allowlist anchoring.** The resolved path must genuinely lie beneath the
   rule's declared root — not merely share a name prefix, and not have
   escaped the root via a `..` traversal. A rule can only ever authorize
   deletion of things actually inside the location it describes.
3. **Denylist veto.** A fixed set of protected locations — user data
   directories, system directories, anything inside a git repository — vetoes
   a candidate even if it passed the first two gates, and even with
   `--force`. The veto cannot be bypassed by configuration or command line.

This ordering means a single misconfigured or overly broad rule cannot, by
itself, put a protected path at risk: gates 2 and 3 exist independently of
gate 1's assertion.

## Time Machine local snapshots

On macOS, Time Machine local snapshots frequently consume more disk space
than everything this tool can remove combined, and **no file deletion
reclaims them**. If a **Time Machine** section appears in the console report,
it means local snapshots were detected; the report states how many, notes
the oldest, and prints the exact `tmutil` commands to inspect and remove
them:

```bash
tmutil listlocalsnapshots /
tmutil deletelocalsnapshots <date>
```

The tool does not run these commands itself — deleting a snapshot is a
system-level operation with its own consequences, outside this tool's
Trash-only safety model. You decide whether and when to run it.

## Adding a rule

The rule catalog in `src/Rules.fsx` is the single source of truth for what
this tool considers disposable. The scanner is parameterized over a plain
`Rule list` and contains no rule-specific logic, so **adding a rule means
appending one record to the catalog — no other file needs to change**.

A rule record looks like this (see `src/Rules.fsx` for the full field list
and existing examples):

```fsharp
{ Name = "Example cache"
  Platforms = Set.ofList [ MacOS ]
  Root = PathSpec.homeRelative "Library/Caches/com.example.App"
  Include = []
  Exclude = []
  Risk = Safe
  Recoverability = Regenerated
  Strategy = ContentsOnly
  MinAge = Some (TimeSpan.FromDays 7.0)
  MinSize = None
  SafeToDelete = true
  Description = "Cache for Example.app"
  Reason = "Regenerated automatically the next time the app runs"
  EstimatedBenefit = "Tens to hundreds of MB"
  Examples = [ "~/Library/Caches/com.example.App/webkit" ]
  Category = Caches }
```

Two invariants are enforced by tests, not by the type system, because they
concern combinations of fields:

- Only a rule rated `Safe` may set `SafeToDelete = true`.
- No `SafeToDelete` rule may target a user-data directory.

Run `dotnet fsi tests/Tests.fsx` after adding a rule — the catalog tests
check name uniqueness, non-empty descriptions, and both invariants above.

## Traversal performance note

Traversal (`src/DirectoryWalker.fsx`) is sequential by design. A full scan
was measured on the development machine at 90s wall clock, 38.6s user time,
36.6s system time. The high system time indicates the workload is
kernel-I/O-bound — exactly the case where thread parallelism helps least —
and the walker feeds directly into the safety-gate pipeline, so the added
complexity and risk of racing it was judged not worth a marginal speedup on
what is normally an occasional, not repeated, tool run.

## Development

```bash
dotnet fsi tests/Tests.fsx   # run the full test suite
```

No file under `src/` or `tests/` contains a `#load` directive except
`tests/Tests.fsx` and `DiskCleanupAdvisor.fsx` — those two are the only load
order owners in the project.

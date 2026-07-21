# Lessons Learned

> Engineering journal capturing what was learned, what went wrong, and what
> tradeoffs were made during development of DiskCleanupAdvisor.

## Environment

- **OS:** macOS 26.5.2 (arm64)
- **SDK:** .NET 10.0.302, F# 9 via `dotnet fsi`
- **Build:** none — script-only project, no `.fsproj`, no NuGet
- **Test runner:** hand-rolled harness at `tests/TestFramework.fsx`, run via `dotnet fsi tests/Tests.fsx`
- **Workflow:** subagent-driven development, 17 planned tasks, 24 commits
- **Last Updated:** 2026-07-21

---

## Lessons

### 2026-07-21 — Environment-privileged blindness: your machine's permissions hide permission bugs

**Insight:** A developer machine configured *more* permissively than the target audience's cannot exercise the code paths that exist for the less-privileged case. This machine has Full Disk Access, so every test of the permission-denial path silently passed through the happy branch.

**Discovery:** During Task 11 a real walk of `~/Library/Caches` returned "15,343 files, 0 errors". I read that as a clean bill of health. It was clean *here*. The final code review flagged that it could not determine from source whether TCC denial throws or returns a silent empty enumeration. A `chmod 000` probe answered it: `Ok, 0 entries`, and the parent walk reported **0 errors**. The tool was presenting a partial scan as a complete one, and the "grant Full Disk Access" hint — a feature built specifically for that case — could never fire.

**Design intent:** `EnumerationOptions(IgnoreInaccessible = true)` was chosen deliberately, and the original comment said why: "lets a partially-readable directory still yield its readable entries instead of failing wholesale." The principle was sound. The assumption underneath it — that this flag only affects *partial* readability — was wrong.

**Impact:** After the fix, a real scan on this machine reports **167 unreadable directories that were previously invisible**, despite Full Disk Access being granted. The bug was hiding itself: its symptom was the absence of a number.

**Active mitigation:** `tests/TestPlatform.fsx` contains a regression test that `chmod 000`s a directory and asserts `EnumerateFiles` returns `Error (PermissionDenied _)`, not `Ok`. It fails if the flag is ever flipped back. The same test asserts a readable sibling still enumerates, so the fix cannot be undone by "restoring" partial-read behaviour.

**Meta-pattern to hunt for:** *environment-privileged blindness*. When reviewing any code with a permission, quota, network, or capability branch, ask which branch the dev machine can actually reach. If the answer is "only the happy one", the unhappy branch is untested regardless of what the suite says.

**Related:** Mistakes — "Permission denial silently swallowed"; Lesson — "Fixing one bug can expose a latent one".

---

### 2026-07-21 — Fixing one bug can expose a latent one that was structurally masked

**Insight:** A bug can be invisible not because it is subtle but because another part of the system's *shape* routes around it. Change the shape and it appears instantly.

**Discovery:** `EnumerationOptions` defaults `AttributesToSkip` to `Hidden ||| System`, and on Unix every dotfile carries `Hidden`. So the walker could never see `~/.nuget`, `~/.npm`, `~/.cache`, `~/.gradle`, `~/.m2`. This was latent from the moment the walker was written in Task 11 — but harmless, because each of those caches was its own **scan root**, and a root is enumerated directly rather than discovered as a child. Deduplicating nested scan roots (Task 13) collapsed 18 roots into one walk from `~`, and `~/.nuget/packages` — 5.2 GB — instantly reported **zero findings**.

**Impact:** Two independent bugs, one masking the other, both in code that had passed per-task review and had green tests. Neither was found by testing; both were found by reading real output and asking why a number looked wrong.

**Bug categorisation — what would actually have caught each:**

| Bug | Cheapest catcher |
|---|---|
| `\` treated as a separator on POSIX | Unit test with a backslash in a filename |
| `$` matching before trailing newline | Unit test with `"name\n"` |
| Chained `**` backtracking | Benchmark assertion in a unit test |
| Nested scan roots → 48% duplicates | **Reading real scan output** — no fixture would have had overlapping roots |
| Hidden dirs skipped | **Real filesystem walk** — in-memory FS has no `Hidden` attribute |
| Permission denial swallowed | **Real filesystem walk under `chmod 000`** |

The first three were recoverable with better unit fixtures. The last three required running against a real filesystem and *distrusting the output*. Do not fold these into one prescription: "write more unit tests" would not have caught the bottom three, and "always test against real disk" is too expensive to apply to the top three.

**Related:** Mistakes — "Hidden directories invisible to the walker", "Nested scan roots double-counted".

---

### 2026-07-21 — `#load` in F# scripts is not idempotent

**Insight:** In `dotnet fsi`, a file reached through two separate `#load` directives is **evaluated twice**, running its top-level side effects twice. Type identity still unifies, so the failure is silent rather than a compile error.

**Discovery:** Probed before writing the implementation plan, precisely because the whole architecture depended on it. Five load topologies were tested. Only one behaves: **no `#load` in any module, with exactly two owner files listing every file once in topological order.** A diamond dependency deduplicated correctly in one shape and duplicated in another, and I could not derive a reliable model for which — so the plan adopted the shape that was empirically proven rather than the one that seemed principled.

**Impact:** The plan's first draft had every module loading its own dependencies, which would have evaluated `Types.fsx` many times per run. 57 `#load` lines were removed from the plan before any code was written.

**Active mitigation:** Audit command — `grep -l '^#load' src/*.fsx src/Report/*.fsx tests/*.fsx DiskCleanupAdvisor.fsx` must return exactly `DiskCleanupAdvisor.fsx` and `tests/Tests.fsx`. Run after any file addition.

**Related:** Compromises — "Script-only architecture".

---

### 2026-07-21 — Verify agent claims against git, never against the agent's report

**Insight:** When delegating work, the report is a claim and the repository is the evidence. They diverged repeatedly.

**Discovery:** Two separate implementer agents reported a commit SHA that was in fact the *previous* commit, having left their work staged but uncommitted. One returned prematurely while waiting on a monitor. One deleted `.superpowers/sdd/probe/` wholesale, destroying controller regression probes from earlier tasks. In every case the returned text was confident and specific.

**Impact:** Had the reports been trusted, Task 3's glob fix and Task 13's entry point would both have been recorded as complete while absent from history.

**Active mitigation:** Every dispatch prompt from Task 4 onward carries: *"Commit, then run `git log --oneline -1` and `git status --short`; confirm the log shows YOUR new commit."* The controller re-runs both independently. Naming the specific prior failure in the prompt measurably improved compliance.

**Related:** Mistakes — "Agent reported a stale commit SHA".

---

### 2026-07-21 — A private constructor makes an invariant structural rather than defended

**Insight:** `DeletionCandidate` is a record with a `private` representation, constructible only inside `RiskEngine.evaluate`. `Cleaner.moveToTrash` accepts `DeletionCandidate list` and never a `string list`. "Move an arbitrary path" is therefore not *prevented* at runtime — it is **unrepresentable**.

**Discovery:** Verified rather than assumed: a probe attempting to forge a candidate fails to compile with `error FS1093: The union cases or fields of the type 'DeletionCandidate' are not accessible from this code location`. Re-verified after the Cleaner landed. The independent final review traced the same path and reached the same conclusion by different means, confirming there is exactly one construction site and no `with`-copy expression anywhere.

**Impact:** The strongest guarantee in the codebase, and the cheapest to audit — it is enforced by the compiler, not by reviewer vigilance.

**Design intent:** Chosen over runtime validation because runtime checks must be *remembered* at every call site, whereas a private constructor cannot be forgotten.

**Related:** Lesson — "Adversarial probing beats assertion-counting".

---

### 2026-07-21 — Adversarial probing beats assertion-counting (recurring diagnostic technique)

**Insight:** The highest-yield verification technique in this project was writing a throwaway probe that *attacks* the code, rather than adding assertions that describe it. This technique caught findings in five separate tasks, which makes it a lesson rather than a coincidence.

**Where it paid:**

| Task | Probe | Outcome |
|---|---|---|
| 2 | 8 adversarial cases against `isDescendantOf` | Confirmed 4-level traversal escape blocked (untested by the suite) |
| 5 | Real tree vs in-memory tree, same assertions | Proved the fake FS matches real semantics on all 6 behaviours |
| 8 | 12 attacks on `evaluate` | All blocked with correct rejections, incl. `--force` bypass attempts |
| 12 | Simulated `~/Library/Containers` layout | Proved sandboxed app data stays non-deletable end-to-end |
| 15 | Real Trash move + restore, then a name-collision test | Proved reversibility and no-overwrite on real disk |

**Impact:** The suite grew from 3 to 172 tests and every task's tests passed at commit time — yet the four most serious defects were found by probes, not tests. Tests encode what you thought of; probes ask what an adversary would do.

**Active mitigation:** Probes are retained under `.superpowers/sdd/probe/` (git-ignored) rather than deleted, so they can be re-run after refactors. `safety.fsx`, `attack.fsx`, and `denied.fsx` are the highest-value three.

**Related:** Lesson — "Environment-privileged blindness"; Mistakes — all four correctness bugs.

---

### 2026-07-21 — `System.Text.Json` cannot serialize F# discriminated unions

**Insight:** `JsonSerializer.Serialize` throws `NotSupportedException` on any F# DU. Records of primitives round-trip fine.

**Discovery:** Probed while writing the plan, before any report code existed. Confirmed again after Task 6.

**Impact:** Task 9 exists solely to provide a DTO projection layer at a single boundary, with the rule *no field of any DTO may be a discriminated union*. Without it, the failure would have surfaced as a runtime crash during report writing, several tasks after the DUs were designed.

**Active mitigation:** `tests/TestDto.fsx` serializes a full `ScanResultDto`; it fails immediately if a DU is introduced into a DTO field.

---

## Mistakes & Fixes

### 2026-07-21 — Permission denial silently swallowed

**Symptom:** Scans reported `0` unreadable directories on every machine, so the "grant Full Disk Access" hint never appeared.

**Attempted:** Initially accepted "15,343 files, 0 errors" from a real walk as evidence the error path worked — see Lesson "Adversarial probing beats assertion-counting". The dedicated unit test *did* cover the denial path, but via an injected fake `FileSystemOps` that returned `Error`, so it tested the walker's handling and not whether the real filesystem ever produces that `Error`.

**Root cause:** `EnumerationOptions(IgnoreInaccessible = true)` in `src/Platform.fsx` makes .NET return an empty result for an unreadable directory instead of throwing.

**Fix:** Flipped to `IgnoreInaccessible = false` (`src/Platform.fsx:113-118`). The mixed case was measured first and proved a non-issue: with the flag false, a parent containing an unreadable file *and* a `chmod 000` subdirectory still returned all 3 readable files and both subdirectories, identical to the previous setting. Only the denied directory itself throws — because the walker descends manually with `RecurseSubdirectories = false`, so each call lists exactly one directory, and on Unix that needs read+execute on that directory alone. Also fixed a doubling introduced by the change: the walker probes each directory twice (files, then subdirectories), so `Scanner.unreadableDirectoryCount` now counts **distinct paths** (`src/Scanner.fsx:265-273`).

**Prevention:** When a test injects a fake to exercise an error path, add a second test that proves the *real* dependency can actually produce that error.

**Active mitigation:** Regression tests in `tests/TestPlatform.fsx` (denial surfaces, readable sibling still enumerates) and `tests/TestScanner.fsx` (distinct-path counting).

**Time Lost:** ~1 hour including the mixed-case measurement.

**Severity:** High — silent under-reporting in a tool whose entire output is a report. Not data loss.

**Related:** Lessons — "Environment-privileged blindness", "Adversarial probing beats assertion-counting".

---

### 2026-07-21 — Hidden directories invisible to the walker

**Symptom:** After deduplicating scan roots, `~/.nuget/packages` (5.2 GB on disk) returned **zero findings**.

**Attempted:** First hypothesis was depth-limit truncation, then a rule-matching failure. Both wrong. A direct probe comparing `EnumerationOptions` defaults settled it in one run.

**Root cause:** `.NET` defaults `AttributesToSkip` to `Hidden ||| System`; on Unix every dotfile carries `Hidden`. Measured: the default returned **53 of 118** directories in the home directory.

**Fix:** `AttributesToSkip = FileAttributes.None` in `src/Platform.fsx`. Findings went 101,645 → 219,719 with duplicates still at zero.

**Prevention:** Read the defaults of any BCL options object rather than assuming unset means unfiltered.

**Active mitigation:** Regression test in `tests/TestPlatform.fsx` creates a dot-directory and asserts it is enumerated.

**Time Lost:** ~40 minutes.

**Severity:** High — silently omitted most of what the tool exists to find.

**Related:** Lesson — "Fixing one bug can expose a latent one".

---

### 2026-07-21 — Nested scan roots double-counted findings

**Symptom:** `Docker.raw` appeared **three times** in the console report's largest-items list.

**Attempted:** Noticed by reading the first real scan output rather than by any test. No test could have caught it: fixtures had non-overlapping roots.

**Root cause:** `Scanner.scan` deduplicated roots with `List.distinct`, which removes identical strings but not roots *nested inside* other roots. Two rules use `Root = HomeRelative ""`, making `~` a scan root containing 17 others.

**Fix:** Filter to outermost roots via `Utilities.Path.isDescendantOf` before walking, plus a defensive `List.distinctBy` on normalized finding paths (`src/Scanner.fsx`).

**Measured impact:** 299,415 findings / 156,791 unique — **48% duplicates**. Reclaimable space reported **28.2 GB** against an actual **15.1 GB**, an 87% overstatement of the one number a user acts on.

**Prevention:** When deduplicating paths, "distinct" almost never means string equality — it means "no element is an ancestor of another".

**Time Lost:** ~30 minutes.

**Severity:** High — materially misleading on the deletion path.

**Related:** Lesson — "Fixing one bug can expose a latent one".

---

### 2026-07-21 — Six CLI flags parsed but never consumed

**Symptom:** `--age 99999` returned results byte-identical to passing no flag.

**Attempted:** Found by the final whole-branch review, not by any per-task review — each flag was correctly *parsed* in Task 13 and each module correctly *built* in Task 14; only a whole-branch view showed the wire was never connected.

**Root cause:** `Cli.Options.Duplicates`, `.LargeFiles`, `.AgeDays`, `.MinSizeBytes`, `.HashAlgorithm` and `.Threads` were read nowhere outside `Cli.fsx`. `src/Duplicates.fsx` and `src/Hashing.fsx` were `#load`ed but never called.

**Fix:** Wired all of them (`ad643e5`); removed `--threads`, which cannot do anything given parallelism was declined. Verified: `--age 99999` → 0 items; `--min-size 100MB` → 28 items vs 248,493 plain; `--duplicates` → 1,501 groups / 21.3 GB wasted.

**Prevention:** Treat "flag documented in help text" as a testable claim. A CLI flag that does nothing is worse than an absent one, because the help text manufactures false confidence — here, a user running `--age 90 --delete` would believe they had narrowed the deletion set and get the full set.

**Active mitigation:** `tests/TestCli.fsx` asserts `--threads` is absent. A stronger mitigation — asserting each remaining flag changes some observable output — is listed as an open gap.

**Time Lost:** ~30 minutes to wire, plus a self-inflicted false alarm (below).

**Severity:** High — safety-relevant false belief on the deletion path.

**Related:** Gaps — "No test proves a CLI flag has an effect"; Mistakes — "Stale output file produced a false alarm".

---

### 2026-07-21 — Stale output file produced a false alarm

**Symptom:** After wiring the CLI flags, I reported them as *still inert* based on JSON output showing identical item counts.

**Attempted:** Concluded the fix had failed and was about to escalate.

**Root cause:** My verification loop reused a single `/tmp/f.json` filename across successive ~2-minute scans and parsed whatever was on disk, without checking each scan's exit code. Checking the console output directly showed `Scanned 1 roots, 0 items` — the flag worked all along.

**Fix:** Re-ran with unique filenames per invocation and confirmed `plain 248,493` vs `--min-size 100MB → 28`.

**Prevention:** Use a unique output path per run and assert the producing command's exit code before parsing its output. Sloppy verification manufactures false alarms as readily as it misses real defects.

**Time Lost:** ~15 minutes, plus an incorrect report to the user that had to be retracted.

**Severity:** Medium — no code impact, but it damaged the reliability of my own reporting.

---

### 2026-07-21 — Agent reported a stale commit SHA

**Symptom:** An implementer agent returned commit `55b75ac` for its fix; `git log` showed `55b75ac` was the *pre-fix* commit and `git status` showed the work staged but uncommitted.

**Attempted:** Nearly recorded the task complete on the strength of the report.

**Root cause:** The agent ran `git add` but never `git commit`, then reported the SHA of `HEAD`.

**Fix:** Controller committed the work after reviewing it. Every later dispatch prompt required the agent to run `git log --oneline -1` and `git status --short` after committing and report what those printed.

**Prevention:** Never accept a delegated agent's claim about repository state. Re-derive it.

**Time Lost:** ~10 minutes; would have been far more had it gone unnoticed.

**Severity:** Medium — caught, but silent work-loss if not.

**Related:** Lesson — "Verify agent claims against git".

---

## Solution Gaps

### 2026-07-21 — No test proves a CLI flag has an observable effect

**Current State:** `tests/TestCli.fsx` asserts flags *parse* into the right `Options` fields, and that `--threads` is gone.

**Limitation:** Parsing correctness is not effect correctness. Six flags parsed perfectly for an entire task cycle while doing nothing, and the existing tests stayed green throughout.

**Ideal Solution:** A test per behavioural flag that runs a scan over an in-memory tree twice — once with, once without — and asserts the results differ in the expected direction.

**Closing this gap requires:**
1. A helper that runs `Scanner.scan` with a given `Config` over a fixed in-memory tree — half an hour — **pending**
2. Assertions for `--age`, `--min-size`, `--duplicates`, `--large-files`, `--hash-algorithm` — an hour — **pending**
3. A guard test asserting every flag in `Cli.helpText` maps to a consumed field — an hour, needs a naming convention to be mechanical — **pending**

**Priority:** High — this is the only gap that let a safety-relevant defect ship.

**Related:** Mistakes — "Six CLI flags parsed but never consumed".

---

### 2026-07-21 — Cleaner's pre-move recheck verifies size only

**Current State:** Before each move, `Cleaner` re-`stat`s the file and skips it if `SizeBytes` differs from what `evaluate` approved (`src/Cleaner.fsx:122-134`).

**Limitation:** It does not re-check `IsDirectory` or `LinkTarget`, and the window is unbounded because the flow blocks on `Console.ReadLine()`. A zero-byte candidate replaced by a directory would match, since directories report size `0`.

**Mitigating measurement:** `Directory.Move` was verified to relocate a **symlink**, not its target — a real directory and its file survived untouched. So the worst case is a Trash move of the wrong entry, which remains reversible. This is why the finding is Important rather than Critical.

**Closing this gap requires:**
1. Assert `current.IsDirectory = false` and `current.LinkTarget.IsNone` in the recheck — 15 minutes — **pending**
2. Re-run `RiskEngine.isVetoed` on the source as defence in depth — 15 minutes — **pending**
3. Correct the docstring at `src/Cleaner.fsx:93-95`, which currently overstates what is checked — 5 minutes — **pending**

**Priority:** Medium.

---

### 2026-07-21 — Exceptions escape `moveToTrash` after the journal is written

**Current State:** `Directory.CreateDirectory trash` (`src/Cleaner.fsx:110`) and the `Exists` probes inside `uniqueDestination` (`:129`) sit outside the `try` beginning at `:130`.

**Limitation:** `moveToTrash` is typed to return `TrashResult` and documented as collecting failures, but a read-only home or a permission failure on `~/.Trash` throws straight out — *after* the journal is written and *after* the user typed `yes`. The user sees a confirmation, then an FSI stack trace, with no summary of what did or did not move.

**Closing this gap requires:**
1. Wrap both call sites and fold failures into `TrashResult.Failed` — 20 minutes — **pending**

**Priority:** Medium — no data loss, but the failure lands at the moment of maximum user anxiety.

---

### 2026-07-21 — `Platform.isElevated` is weaker than advertised

**Current State:** Checks `USER = "root"` and the presence of `SUDO_USER` (`src/Platform.fsx:47-50`). The comment claims "getuid() returns 0 for root", which the code does not do.

**Limitation:** Defeated by `su -`, a root launchd/cron context, or `env -u USER`. `--help` advertises refusal-to-run-elevated as a safety property.

**Mitigating factor:** The denylist veto protects `/System`, `/Applications`, `/etc` regardless of elevation, so this is not a data-loss path.

**Closing this gap requires:**
1. Correct the comment to describe the heuristic honestly — 5 minutes — **pending**
2. Add a `HOME = "/var/root"` check — 10 minutes — **pending**

**Priority:** Low.

---

### 2026-07-21 — Time Machine snapshot reporting never ran against a live snapshot

**Current State:** `src/Snapshots.fsx` parses `tmutil listlocalsnapshots /` and renders advice.

**Limitation:** `tmutil` reported one snapshot early in the session and **zero** by the end — macOS thinned it. The live branch was therefore never exercised end-to-end. Verified synthetically using the exact observed name format (`com.apple.TimeMachine.2026-07-20-110340.local`): counts render, `tmutil` commands print, the disclaimer appears, empty list yields empty string.

**Closing this gap requires:**
1. Re-run `dotnet fsi DiskCleanupAdvisor.fsx -- --scan` when a snapshot exists and confirm the section appears — 5 minutes, opportunistic — **pending**

**Priority:** Low — parsing is verified against the real format; only the live plumbing is unconfirmed.

---

## Compromises

### 2026-07-21 — Parallel traversal declined despite a 90-second scan

**Tradeoff:** The plan specified adding bounded-parallel traversal if a scan exceeded ~10 seconds. Measured **90s wall, 38.6s user, 36.6s sys**. Parallelism was not implemented.

**Reason:** `sys` time nearly equals `user` time, indicating a kernel-I/O-bound workload where thread parallelism helps least. `DirectoryWalker` feeds the safety gate; introducing concurrency into it risks races in the component whose correctness matters most, for perhaps 2× on a tool run occasionally.

**Impact:** Scans take ~90 seconds on a 220k-file home directory. Acceptable for occasional use; would be irritating in a watch loop.

**Prevention going forward OR accepted recurrence:** **Accepted recurrence.** The decision is recorded in the header comment of `src/DirectoryWalker.fsx` together with the measurement, so the next contributor inherits the reasoning rather than re-deriving it. `--threads` was removed from the CLI rather than left as an inert flag.

**Revisit When:** the walker is no longer the sole feeder of the risk gate, **or** a measurement shows `user` time dominating `sys` time, **or** a use case appears that runs scans in a loop.

**Related:** Mistakes — "Six CLI flags parsed but never consumed" (`--threads` was one of them).

---

### 2026-07-21 — Windows `--delete` refuses rather than falling back

**Tradeoff:** On Windows, `--delete` returns an error instead of removing files. Recycle Bin support needs `SHFileOperation` P/Invoke, which is out of scope for a script-only project.

**Reason:** The only fallback available without P/Invoke is an irreversible unlink, which would defeat the tool's central guarantee.

**Impact:** Windows users can scan and report but not clean.

**Prevention going forward OR accepted recurrence:** **Accepted recurrence.** Refusing is the correct behaviour and should persist even after P/Invoke lands — the rule is that no platform may acquire a delete path that is not reversible. `src/Cleaner.fsx`'s `trashRoot` returns `Error` for Windows with an explanatory message rather than silently degrading.

**Revisit When:** someone needs Windows cleanup badly enough to add and test the P/Invoke.

---

### 2026-07-21 — Script-only architecture with a hand-rolled test harness

**Tradeoff:** No `.fsproj`, no xUnit/FsCheck/FsUnit, no `dotnet test`, no coverage tooling. The project's own F# rules recommend xUnit + FsUnit + FsCheck and an 80% coverage target; none of that is measurable here.

**Reason:** The originating spec required a runnable `.fsx` with no third-party dependencies. Property-based testing via FsCheck would have suited the path-normalization and glob code particularly well.

**Impact:** 172 tests, but coverage is unmeasured and assertion vocabulary is limited to what `tests/TestFramework.fsx` provides. The `#load` non-idempotency constraint exists solely because of this choice.

**Prevention going forward OR accepted recurrence:** **Accepted recurrence** for as long as "single script, no dependencies" remains a requirement. The structural marker is that exactly two files may contain `#load` — auditable with `grep -l '^#load' src/*.fsx src/Report/*.fsx tests/*.fsx DiskCleanupAdvisor.fsx`, which must return exactly two paths.

**Revisit When:** the no-dependency requirement is relaxed. The module boundaries were designed to port to a `.fsproj` without restructuring — each module is already a single file with explicit dependencies.

**Related:** Lesson — "`#load` in F# scripts is not idempotent".

---

### 2026-07-21 — Duplicate detection reports but never proposes deletion

**Tradeoff:** `--duplicates` found 1,501 groups wasting 21.3 GB, and not one byte is offered for automatic removal.

**Reason:** Identical bytes do not make a copy disposable. The second copy may be the one a running program references, or may live inside a backup the user relies on. The tool cannot distinguish these.

**Impact:** The largest single reclaimable figure the tool can identify is one it will not act on. Users must resolve duplicates manually.

**Prevention going forward OR accepted recurrence:** **Accepted recurrence, permanently.** `src/Duplicates.fsx` does not reference `RiskEngine` at all, so duplicate groups cannot become `DeletionCandidate`s by construction. A test asserts `--delete` offers an identical item count with and without `--duplicates` (verified: 92,967 items both ways).

**Revisit When:** never, unless the tool gains a way to prove a copy is unreferenced — which would require filesystem-level usage tracking it does not have.

---

## Archive

> Entries moved here when the underlying condition no longer applies.
> Kept for historical context.

_(none yet)_

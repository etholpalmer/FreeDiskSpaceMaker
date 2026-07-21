(**
    Tests for Scanner: rule matching in precedence order, risk-gated
    candidates, separate reclaimable/pending-review totals, config filters,
    and override rejection.
*)
open System
open TestFramework
open Types
open Platform
open Scanner
open Rules

/// A minimal rule builder for tests.
let private testRule name root risk safeToDelete includes : Rule =
    { Name = name
      Platforms = Set.ofList [ MacOS; Linux; Windows ]
      Root = Absolute root
      Include = includes
      Exclude = []
      Risk = risk
      Recoverability = Regenerated
      Strategy = ContentsOnly
      MinAge = None
      MinSize = None
      SafeToDelete = safeToDelete
      Description = "test rule"
      Reason = "test reason"
      EstimatedBenefit = "test"
      Examples = [ root ]
      Category = Caches }

let private tree =
    [ { Path = "/"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/cache"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/cache/a.tmp"; SizeBytes = 1000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 }
      { Path = "/cache/b.tmp"; SizeBytes = 2000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 60.0 }
      { Path = "/data"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
      { Path = "/data/keep.txt"; SizeBytes = 3000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 } ]

let run () =
    suite "Scanner"

    let fs = FileSystem.inMemory tree
    let config = Configuration.defaults

    test "a Safe rule produces findings with deletion candidates" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan fs config false false rules) "scan succeeds"
        assertEqual 2 result.Findings.Length "two cache files found"
        assertTrue (result.Findings |> List.forall (fun f -> f.Candidate.IsSome)) "all have candidates")

    test "a Review rule produces findings with no candidates" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Data" "/data" Review false []; Root = "/data" } ]
        let result = assertOk (scan fs config false false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "one file found"
        assertEqual None (List.head result.Findings).Candidate "no candidate for a Review rule")

    test "reclaimable and pending-review bytes are reported separately" (fun () ->
        // Conflating these would let a user believe space was freed when it
        // had only been listed for review.
        let rules : ResolvedRule list =
            [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" }
              { Rule = testRule "Data" "/data" Review false []; Root = "/data" } ]
        let result = assertOk (scan fs config false false rules) "scan succeeds"
        assertEqual 3000L (reclaimableBytes result) "cache bytes are reclaimable"
        assertEqual 3000L (pendingReviewBytes result) "data bytes are pending review")

    test "the first rule in precedence order wins" (fun () ->
        // This is the Containers requirement: a narrow Safe rule must beat a
        // broad Review catch-all covering the same path.
        let narrow : ResolvedRule = { Rule = testRule "Narrow" "/cache" Safe true []; Root = "/cache" }
        let broad : ResolvedRule = { Rule = testRule "Broad" "/" Review false []; Root = "/" }
        let ordered = Rules.byPrecedence [ broad; narrow ]
        let result = assertOk (scan fs config false false ordered) "scan succeeds"
        let cacheFinding = result.Findings |> List.find (fun f -> f.Meta.Path = "/cache/a.tmp")
        assertEqual "Narrow" cacheFinding.RuleName "the more specific rule matched")

    test "MinAge excludes items younger than the threshold" (fun () ->
        let aged = { testRule "Cache" "/cache" Safe true [] with MinAge = Some (TimeSpan.FromDays 30.0) }
        let rules : ResolvedRule list = [ { Rule = aged; Root = "/cache" } ]
        let result = assertOk (scan fs config false false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "only the 60-day-old file qualifies"
        assertEqual "/cache/b.tmp" (List.head result.Findings).Meta.Path "the older file")

    test "MinSize excludes items below the threshold" (fun () ->
        let sized = { testRule "Cache" "/cache" Safe true [] with MinSize = Some 1500L }
        let rules : ResolvedRule list = [ { Rule = sized; Root = "/cache" } ]
        let result = assertOk (scan fs config false false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "only the 2000-byte file qualifies")

    test "include globs restrict what a rule matches" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true [ "*.nomatch" ]; Root = "/cache" } ]
        let result = assertOk (scan fs config false false rules) "scan succeeds"
        assertEqual 0 result.Findings.Length "no file matches the include glob")

    test "ignored extensions from config are excluded" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let withIgnore = { config with IgnoreExtensions = [ ".tmp" ] }
        let result = assertOk (scan fs withIgnore false false rules) "scan succeeds"
        assertEqual 0 result.Findings.Length "tmp files excluded by config")

    test "a lowering risk override aborts the scan with an error" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Data" "/data" Review false []; Root = "/data" } ]
        let bad = { config with RiskOverrides = Map.ofList [ "Data", Safe ] }
        let errors = assertError (scan fs bad false false rules) "lowering override rejected"
        assertContains "lower" ((List.head errors).ToLowerInvariant()) "explains the direction")

    test "byCategory groups findings and drops empty categories" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan fs config false false rules) "scan succeeds"
        let grouped = byCategory result
        assertEqual 1 grouped.Length "one non-empty category"
        assertEqual 2 (snd (List.head grouped)).Length "two findings in it")

    test "nested roots do not double-count a file" (fun () ->
        let nestedTree =
            [ { Path = "/outer"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/outer/inner"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/outer/inner/f.tmp"; SizeBytes = 500L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 } ]
        let nestedFs = FileSystem.inMemory nestedTree
        let rules : ResolvedRule list =
            [ { Rule = testRule "Outer" "/outer" Safe true []; Root = "/outer" }
              { Rule = testRule "Inner" "/outer/inner" Safe true []; Root = "/outer/inner" } ]
        let result = assertOk (scan nestedFs config false false rules) "scan succeeds"
        let matches = result.Findings |> List.filter (fun f -> f.Meta.Path = "/outer/inner/f.tmp")
        assertEqual 1 matches.Length "the file is reported exactly once")

    test "nested roots do not inflate reclaimable bytes" (fun () ->
        let nestedTree =
            [ { Path = "/outer"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/outer/inner"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/outer/inner/f.tmp"; SizeBytes = 500L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 } ]
        let nestedFs = FileSystem.inMemory nestedTree
        let rules : ResolvedRule list =
            [ { Rule = testRule "Outer" "/outer" Safe true []; Root = "/outer" }
              { Rule = testRule "Inner" "/outer/inner" Safe true []; Root = "/outer/inner" } ]
        let result = assertOk (scan nestedFs config false false rules) "scan succeeds"
        assertEqual 500L (reclaimableBytes result) "the file's size is counted exactly once")

    test "nested-root deduplication does not lose coverage" (fun () ->
        let nestedTree =
            [ { Path = "/outer"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/outer/shallow.tmp"; SizeBytes = 100L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 }
              { Path = "/outer/inner"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/outer/inner/deep.tmp"; SizeBytes = 500L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 } ]
        let nestedFs = FileSystem.inMemory nestedTree
        let rules : ResolvedRule list =
            [ { Rule = testRule "Outer" "/outer" Safe true []; Root = "/outer" }
              { Rule = testRule "Inner" "/outer/inner" Safe true []; Root = "/outer/inner" } ]
        let result = assertOk (scan nestedFs config false false rules) "scan succeeds"
        assertTrue (result.Findings |> List.exists (fun f -> f.Meta.Path = "/outer/shallow.tmp")) "the shallow file is still found"
        assertTrue (result.Findings |> List.exists (fun f -> f.Meta.Path = "/outer/inner/deep.tmp")) "the file only present under the deeper root is still found")

    test "sibling non-nested roots are both walked" (fun () ->
        let siblingTree =
            [ { Path = "/a"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/a/one.tmp"; SizeBytes = 100L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 }
              { Path = "/b"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/b/two.tmp"; SizeBytes = 200L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 } ]
        let siblingFs = FileSystem.inMemory siblingTree
        let rules : ResolvedRule list =
            [ { Rule = testRule "A" "/a" Safe true []; Root = "/a" }
              { Rule = testRule "B" "/b" Safe true []; Root = "/b" } ]
        let result = assertOk (scan siblingFs config false false rules) "scan succeeds"
        assertTrue (result.Findings |> List.exists (fun f -> f.Meta.Path = "/a/one.tmp")) "sibling A's file is found"
        assertTrue (result.Findings |> List.exists (fun f -> f.Meta.Path = "/b/two.tmp")) "sibling B's file is found")

    test "walk errors are carried into the result" (fun () ->
        let denying = { fs with EnumerateFiles = fun p -> Error (PermissionDenied p) }
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan denying config false false rules) "scan still succeeds"
        assertTrue (result.Errors.Length > 0) "errors reported"
        assertEqual 0 result.Findings.Length "no findings from an unreadable tree")

    // --- Global --age and --min-size floors (config.AgeThresholdDays / config.FindingsMinSizeBytes) ---
    // Regression coverage for the defect where these flags parsed but were
    // never consulted by the scanner: --age 99999 returned the same item
    // count as no flag at all.

    test "a global age floor excludes items younger than the threshold and keeps older ones" (fun () ->
        let aged = { config with AgeThresholdDays = 30 }
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan fs aged false false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "only the 60-day-old file clears the 30-day global floor"
        assertEqual "/cache/b.tmp" (List.head result.Findings).Meta.Path "the older file survives")

    test "a global age floor of zero changes nothing versus no floor" (fun () ->
        let zeroFloor = { config with AgeThresholdDays = 0 }
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let withZero = assertOk (scan fs zeroFloor false false rules) "scan with explicit zero"
        let withoutFlag = assertOk (scan fs config false false rules) "scan with the default config"
        assertEqual withoutFlag.Findings.Length withZero.Findings.Length "identical item count"
        assertEqual (reclaimableBytes withoutFlag) (reclaimableBytes withZero) "identical reclaimable bytes")

    test "a global findings size floor excludes smaller items and keeps larger ones" (fun () ->
        let floored = { config with FindingsMinSizeBytes = 1500L }
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan fs floored false false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "only the 2000-byte file clears the 1500-byte global floor"
        assertEqual "/cache/b.tmp" (List.head result.Findings).Meta.Path "the larger file survives")

    test "a plain scan is unaffected by the new global floors' defaults" (fun () ->
        // The safety-critical regression check: defaults for both new floors
        // (AgeThresholdDays = 0, FindingsMinSizeBytes = 0) must not narrow a
        // plain scan versus the unmodified defaults record.
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan fs config false false rules) "scan succeeds"
        assertEqual 2 result.Findings.Length "both cache files still found with no flags")

    // --- --duplicates: a separate, Review-only section (see Scanner.scan) ---

    test "duplicates are grouped, reported separately, and never gain a deletion candidate" (fun () ->
        let dupTree =
            [ { Path = "/dup"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
              { Path = "/dup/a.bin"; SizeBytes = 4096L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 10.0 }
              { Path = "/dup/b.bin"; SizeBytes = 4096L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0 } ]
        let dupFs = FileSystem.inMemory dupTree
        // Lowered so the 4096-byte fixture files clear the duplicate-hashing
        // floor; the default (1 MB) is for the hasher, not for this test.
        let dupConfig = { config with MinSizeBytes = 1024L }
        let rules : ResolvedRule list = [ { Rule = testRule "Dup" "/dup" Safe true []; Root = "/dup" } ]

        let withoutDuplicates = assertOk (scan dupFs dupConfig false false rules) "scan without --duplicates"
        let withDuplicates = assertOk (scan dupFs dupConfig false true rules) "scan with --duplicates"

        assertEqual 0 withoutDuplicates.Duplicates.Length "no duplicate groups computed when the flag is off"
        assertEqual 1 withDuplicates.Duplicates.Length "one duplicate group computed when the flag is on"
        assertEqual 2 (List.head withDuplicates.Duplicates).Paths.Length "both identical files grouped"

        // The safety assertion: enabling --duplicates must not change which
        // findings have a deletion candidate, nor the reclaimable total.
        // Identical bytes do not make a copy disposable.
        assertEqual (reclaimableBytes withoutDuplicates) (reclaimableBytes withDuplicates)
            "reclaimableBytes is unchanged by --duplicates"
        assertTrue
            (withDuplicates.Findings |> List.forall (fun f -> f.Candidate.IsSome))
            "every finding still gets a candidate from its own Safe rule, independent of duplicate grouping"
        assertTrue (withDuplicates.Duplicates |> List.forall (fun g -> g.Wasted > 0L)) "wasted bytes are reported")

    test "unreadable directories are counted once, not once per probe" (fun () ->
        // The walker probes each directory twice — files, then subdirectories —
        // so one unreadable directory produces two PermissionDenied errors.
        // The Full Disk Access hint must report directories, not error records.
        let denied = Platform.FileSystem.inMemory []
        let result : ScanResult =
            { GeneratedAtUtc = DateTime.UtcNow
              Platform = Platform.current ()
              ScanRoots = [ "/x" ]
              Findings = []
              Duplicates = []
              Errors =
                [ PermissionDenied "/x/locked"
                  PermissionDenied "/x/locked"
                  PermissionDenied "/x/other"
                  PathNotFound "/x/gone" ]
              Elapsed = TimeSpan.Zero
              Counters = Diagnostics.snapshot (Diagnostics.create ()) }
        ignore denied
        assertEqual 2 (unreadableDirectoryCount result) "two distinct directories, not three errors")


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
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 2 result.Findings.Length "two cache files found"
        assertTrue (result.Findings |> List.forall (fun f -> f.Candidate.IsSome)) "all have candidates")

    test "a Review rule produces findings with no candidates" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Data" "/data" Review false []; Root = "/data" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "one file found"
        assertEqual None (List.head result.Findings).Candidate "no candidate for a Review rule")

    test "reclaimable and pending-review bytes are reported separately" (fun () ->
        // Conflating these would let a user believe space was freed when it
        // had only been listed for review.
        let rules : ResolvedRule list =
            [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" }
              { Rule = testRule "Data" "/data" Review false []; Root = "/data" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 3000L (reclaimableBytes result) "cache bytes are reclaimable"
        assertEqual 3000L (pendingReviewBytes result) "data bytes are pending review")

    test "the first rule in precedence order wins" (fun () ->
        // This is the Containers requirement: a narrow Safe rule must beat a
        // broad Review catch-all covering the same path.
        let narrow : ResolvedRule = { Rule = testRule "Narrow" "/cache" Safe true []; Root = "/cache" }
        let broad : ResolvedRule = { Rule = testRule "Broad" "/" Review false []; Root = "/" }
        let ordered = Rules.byPrecedence [ broad; narrow ]
        let result = assertOk (scan fs config false ordered) "scan succeeds"
        let cacheFinding = result.Findings |> List.find (fun f -> f.Meta.Path = "/cache/a.tmp")
        assertEqual "Narrow" cacheFinding.RuleName "the more specific rule matched")

    test "MinAge excludes items younger than the threshold" (fun () ->
        let aged = { testRule "Cache" "/cache" Safe true [] with MinAge = Some (TimeSpan.FromDays 30.0) }
        let rules : ResolvedRule list = [ { Rule = aged; Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "only the 60-day-old file qualifies"
        assertEqual "/cache/b.tmp" (List.head result.Findings).Meta.Path "the older file")

    test "MinSize excludes items below the threshold" (fun () ->
        let sized = { testRule "Cache" "/cache" Safe true [] with MinSize = Some 1500L }
        let rules : ResolvedRule list = [ { Rule = sized; Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 1 result.Findings.Length "only the 2000-byte file qualifies")

    test "include globs restrict what a rule matches" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true [ "*.nomatch" ]; Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        assertEqual 0 result.Findings.Length "no file matches the include glob")

    test "ignored extensions from config are excluded" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let withIgnore = { config with IgnoreExtensions = [ ".tmp" ] }
        let result = assertOk (scan fs withIgnore false rules) "scan succeeds"
        assertEqual 0 result.Findings.Length "tmp files excluded by config")

    test "a lowering risk override aborts the scan with an error" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Data" "/data" Review false []; Root = "/data" } ]
        let bad = { config with RiskOverrides = Map.ofList [ "Data", Safe ] }
        let errors = assertError (scan fs bad false rules) "lowering override rejected"
        assertContains "lower" ((List.head errors).ToLowerInvariant()) "explains the direction")

    test "byCategory groups findings and drops empty categories" (fun () ->
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan fs config false rules) "scan succeeds"
        let grouped = byCategory result
        assertEqual 1 grouped.Length "one non-empty category"
        assertEqual 2 (snd (List.head grouped)).Length "two findings in it")

    test "walk errors are carried into the result" (fun () ->
        let denying = { fs with EnumerateFiles = fun p -> Error (PermissionDenied p) }
        let rules : ResolvedRule list = [ { Rule = testRule "Cache" "/cache" Safe true []; Root = "/cache" } ]
        let result = assertOk (scan denying config false rules) "scan still succeeds"
        assertTrue (result.Errors.Length > 0) "errors reported"
        assertEqual 0 result.Findings.Length "no findings from an unreadable tree")

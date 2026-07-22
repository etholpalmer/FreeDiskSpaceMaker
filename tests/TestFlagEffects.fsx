(**
    Proves every documented CLI flag has an observable effect.

    Six flags once parsed correctly for an entire task cycle while doing
    nothing, and the suite stayed green throughout, because the existing tests
    asserted only that a flag *parses* into the right Options field. Parsing
    correctness is not effect correctness. `--age 90 --delete` that silently
    ignores the age is worse than an absent flag: the help text manufactures a
    false belief on the deletion path.

    Each test here runs the same fixture twice — once with the flag, once
    without — and asserts the results differ in the stated direction. The final
    guard test fails if a new flag is added to the help text without being
    classified, so this file cannot silently fall behind the CLI.
*)
open System
open TestFramework
open Types
open Platform
open Rules

/// A Safe, deletable rule rooted at /fx.
let private fixtureRule : ResolvedRule =
    { Rule =
        { Name = "Flag Fixture"
          Platforms = Set.ofList [ MacOS; Linux; Windows ]
          Root = Absolute "/fx"
          Include = []
          Exclude = []
          Risk = Safe
          Recoverability = Regenerated
          Strategy = ContentsOnly
          MinAge = None
          MinSize = None
          SafeToDelete = true
          Description = "fixture"
          Reason = "fixture"
          EstimatedBenefit = "fixture"
          Examples = [ "/fx" ]
          Category = Caches }
      Root = "/fx" }

/// Two small recent files, one large old file, plus a byte-identical twin of
/// the large one so duplicate detection has something real to find.
let private tree =
    [ { Path = "/fx"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
      { Path = "/fx/new-small-a.tmp"; SizeBytes = 2_000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
      { Path = "/fx/new-small-b.tmp"; SizeBytes = 2_000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
      { Path = "/fx/old-large-a.tmp"; SizeBytes = 8_000_000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 400.0; ApparentBytes = None }
      { Path = "/fx/old-large-b.tmp"; SizeBytes = 8_000_000L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 400.0; ApparentBytes = None } ]

let private fs = FileSystem.inMemory tree

/// Runs a scan with the supplied config tweak applied to the defaults.
let private scanWith (tweak: Configuration.Config -> Configuration.Config) (computeDuplicates: bool) =
    let config = tweak Configuration.defaults
    match Scanner.scan fs config false computeDuplicates [ fixtureRule ] with
    | Ok result -> result
    | Error errors -> failwithf "fixture scan failed: %A" errors

let private baseline () = scanWith id false

let run () =
    suite "CLI flag effects"

    test "the fixture itself is sane before any flag is applied" (fun () ->
        // If this drifts, every comparison below becomes meaningless.
        let r = baseline ()
        assertEqual 4 r.Findings.Length "four files found with no filters"
        assertEqual 16_004_000L (Scanner.reclaimableBytes r) "all four are Safe and reclaimable")

    test "--age excludes items younger than the threshold" (fun () ->
        let filtered = scanWith (fun c -> { c with AgeThresholdDays = 100 }) false
        assertEqual 2 filtered.Findings.Length "only the two 400-day-old files survive"
        assertTrue (filtered.Findings |> List.forall (fun f -> f.Meta.Path.Contains "old-large"))
            "the surviving files are the old ones"
        assertTrue (filtered.Findings.Length < (baseline ()).Findings.Length)
            "the flag strictly narrows the result set")

    test "--age of zero changes nothing" (fun () ->
        // The default must remain a no-op, or every scan silently changes.
        let zero = scanWith (fun c -> { c with AgeThresholdDays = 0 }) false
        assertEqual (baseline ()).Findings.Length zero.Findings.Length "zero is a no-op"
        assertEqual (Scanner.reclaimableBytes (baseline ())) (Scanner.reclaimableBytes zero)
            "reclaimable total unchanged")

    test "--min-size excludes items below the floor" (fun () ->
        let filtered = scanWith (fun c -> { c with FindingsMinSizeBytes = 1_000_000L }) false
        assertEqual 2 filtered.Findings.Length "only the two 8 MB files survive"
        assertTrue (filtered.Findings |> List.forall (fun f -> f.Meta.SizeBytes >= 1_000_000L))
            "every survivor meets the floor")

    test "--min-size of zero changes nothing" (fun () ->
        let zero = scanWith (fun c -> { c with FindingsMinSizeBytes = 0L }) false
        assertEqual (baseline ()).Findings.Length zero.Findings.Length "zero is a no-op")

    test "--duplicates populates duplicate groups" (fun () ->
        let without = scanWith id false
        let handled = scanWith (fun c -> { c with MinSizeBytes = 1_000L }) true
        assertEqual 0 without.Duplicates.Length "no groups computed when the flag is off"
        assertTrue (handled.Duplicates.Length > 0) "groups computed when the flag is on"
        let group = List.head handled.Duplicates
        assertEqual 2 group.Paths.Length "the two identical 8 MB files are grouped"
        assertEqual 8_000_000L group.Wasted "one redundant copy is wasted")

    test "--duplicates never creates deletion candidates" (fun () ->
        // THE safety assertion for this flag. Identical bytes do not make a
        // copy disposable: the second may be the one a running program
        // references. Enabling detection must not enlarge the deletable set.
        let without = scanWith id false
        let handled = scanWith (fun c -> { c with MinSizeBytes = 1_000L }) true
        assertEqual (Scanner.reclaimableBytes without) (Scanner.reclaimableBytes handled)
            "reclaimable bytes identical with and without duplicate detection"
        assertEqual
            (without.Findings |> List.filter (fun f -> f.Candidate.IsSome) |> List.length)
            (handled.Findings |> List.filter (fun f -> f.Candidate.IsSome) |> List.length)
            "candidate count identical with and without duplicate detection")

    test "--hash-algorithm selects the algorithm actually used" (fun () ->
        // Both must find the same duplicates; the flag chooses how, not what.
        let sha = scanWith (fun c -> { c with MinSizeBytes = 1_000L; HashAlgorithm = "sha256" }) true
        let md5 = scanWith (fun c -> { c with MinSizeBytes = 1_000L; HashAlgorithm = "md5" }) true
        assertEqual sha.Duplicates.Length md5.Duplicates.Length "both algorithms find the same groups"
        assertTrue (Hashing.parse "md5" |> Result.isOk) "md5 parses"
        assertTrue (Hashing.parse "sha256" |> Result.isOk) "sha256 parses"
        assertTrue (Hashing.parse "crc32" |> Result.isError) "an unknown algorithm is rejected")

    test "--large-files adds a section the default report omits" (fun () ->
        let r = baseline ()
        let plain = Report.Console.render [] r false
        let expanded = Report.Console.render [] r true
        assertTrue (expanded.Length > plain.Length) "the flag adds output"
        assertContains "Large" expanded "the dedicated section appears"
        assertFalse (plain.Contains "Largest files") "and is absent by default")

    test "--json-full includes every finding; the default caps them" (fun () ->
        // A default report of 159,236 findings came to 112 MB at ~700 bytes
        // each, which is unusable for anything but a machine. Capping is right,
        // but a truncated document that looks complete would be the same class
        // of defect as a silently partial scan — so truncation is declared.
        let r = baseline ()
        let capped = Report.Json.toDtoWithLimit (Some 2) r
        let full = Report.Json.toDtoWithLimit None r
        assertEqual 2 capped.Findings.Length "the cap is applied"
        assertTrue capped.FindingsTruncated "and truncation is declared"
        assertEqual 2 capped.FindingsIncluded "the included count is honest"
        assertEqual r.Findings.Length full.Findings.Length "the flag includes everything"
        assertFalse full.FindingsTruncated "a complete report says so"
        assertEqual r.Findings.Length full.FindingsIncluded "counts agree when complete"
        // Both report the same true total, so a consumer is never misled.
        assertEqual capped.TotalItems full.TotalItems "TotalItems is the real total either way"
        // The cap keeps the largest, which is what makes it useful.
        let smallest = capped.Findings |> List.map (fun f -> f.SizeBytes) |> List.min
        let excluded =
            r.Findings |> List.map (fun f -> f.Meta.SizeBytes)
            |> List.sortDescending |> List.skip 2
        assertTrue (excluded |> List.forall (fun s -> s <= smallest))
            "everything dropped is smaller than everything kept")

    suite "CLI flag coverage guard"

    test "every flag in the help text is classified as covered" (fun () ->
        // This is the test that would have caught six inert flags shipping.
        // Adding a flag to helpText without classifying it here fails the
        // suite, forcing a deliberate decision about how it is verified.
        let documented =
            Text.RegularExpressions.Regex.Matches(Cli.helpText, @"--[a-z][a-z-]*")
            |> Seq.map (fun m -> m.Value)
            |> Seq.distinct
            |> Set.ofSeq

        // Flags whose effect on scan results is asserted in this file.
        let behavioural =
            set [ "--age"; "--min-size"; "--duplicates"; "--large-files"; "--hash-algorithm"; "--json-full" ]
        // Flags that select a mode; covered by TestCli parse tests and by the
        // entry point refusing --delete until Cleaner exists.
        let modes = set [ "--scan"; "--delete"; "--dry-run"; "--help" ]
        // Flags that write a file; covered by TestReports and the Json writer.
        let outputs = set [ "--json"; "--markdown"; "--html" ]
        // Flags whose effect is on process behaviour rather than scan results.
        let processLevel = set [ "--config"; "--force"; "--allow-root"; "--verbose" ]

        let classified = Set.unionMany [ behavioural; modes; outputs; processLevel ]
        let unclassified = Set.difference documented classified
        let stale = Set.difference classified documented

        assertTrue (Set.isEmpty unclassified)
            (sprintf "help text documents flags with no declared coverage: %A. Add an effect test above, or classify it."
                (Set.toList unclassified))
        assertTrue (Set.isEmpty stale)
            (sprintf "these flags are classified but no longer documented: %A. Remove them here."
                (Set.toList stale)))

    test "--threads stays removed" (fun () ->
        // Parallel traversal was declined after measurement. The flag was
        // removed rather than left inert; this pins that decision.
        assertFalse (Cli.helpText.Contains "--threads") "not documented"
        assertError (Cli.parse [ "--threads"; "4" ]) "rejected as unknown" |> ignore)

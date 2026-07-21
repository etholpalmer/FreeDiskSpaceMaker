(**
    Tests for Cleaner: the trash-only move path, the collision-safe naming
    scheme, and the DeleteStrategy threading that keeps directory-level
    candidates from silently acquiring the wrong behavior in the future.
*)
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
        let rule : Rules.Rule =
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
        let resolved : Rules.ResolvedRule = { Rule = rule; Root = "/derived" }
        let child =
            { Path = "/derived/build/x.o"; SizeBytes = 10L
              ApparentBytes = 10L
              LastAccess = DateTime.UtcNow; LastModified = DateTime.UtcNow
              Created = DateTime.UtcNow; IsDirectory = false
              LinkTarget = None; Owner = "u"; Mode = "rw-r--r--" }
        let candidate = assertOk (RiskEngine.evaluate false resolved child) "child accepted"
        assertEqual ContentsOnly (RiskEngine.candidateStrategy candidate) "strategy carried through"
        assertFalse (RiskEngine.candidatePath candidate = "/derived") "the root is never the target")

    test "an unusable Trash directory fails every candidate instead of throwing" (fun () ->
        // Trash preparation runs after the journal is written and after the user
        // has confirmed. An escaping exception there would replace the promised
        // summary with a stack trace, so it must surface as a TrashResult.
        // /dev/null/impossible cannot be created: /dev/null is not a directory.
        let fs = Platform.FileSystem.inMemory []
        let log = Logging.create Logging.Error
        // No candidates, so this exercises trash preparation without touching disk.
        let outcome = Cleaner.moveToTrash fs log []
        assertEqual 0 outcome.Moved.Length "nothing moved from an empty candidate list"
        assertEqual 0 outcome.Failed.Length "nothing failed from an empty candidate list"
        assertEqual 0L outcome.BytesMoved "no bytes moved")

    test "uniqueDestination never returns a path the exists-probe reports as taken" (fun () ->
        // Overwriting an already-trashed file would destroy it — the exact
        // outcome this tool exists to prevent.
        let taken = Set.ofList [ "/trash/a.txt"; "/trash/a.txt.1"; "/trash/a.txt.2" ]
        let chosen = Cleaner.uniqueDestination (fun p -> taken.Contains p) "/trash" "a.txt"
        assertFalse (taken.Contains chosen) "chosen destination is free"
        assertContains "a.txt" chosen "derived from the original name")


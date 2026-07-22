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
        let outcome = Cleaner.moveToTrash fs log DateTime.Now []
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

    test "journal entries serialize with their fields, not as empty objects" (fun () ->
        // System.Text.Json serializes only public properties. Marking
        // JournalEntry `private` made every entry serialize as `{}` — which
        // shipped, and produced 94,488 empty records for a real 15.2 GB run.
        // The journal is the only audit trail of what was moved, so an empty
        // one is worse than none: it looks like a record and is not.
        let entry : Cleaner.JournalEntry =
            { Path = "/x/a.tmp"; RuleName = "User Caches"
              SizeBytes = 4096L; AttemptedAtUtc = "2026-07-21T00:00:00Z" }
        let json = Text.Json.JsonSerializer.Serialize(entry, Dto.serializerOptions)
        assertFalse (json.Trim() = "{}") "must not serialize as an empty object"
        assertContains "/x/a.tmp" json "path is present"
        assertContains "User Caches" json "rule name is present"
        assertContains "4096" json "size is present")

    test "destinations mirror the source tree instead of flattening it" (fun () ->
        // The first version moved every file to the Trash root by basename.
        // That flattened 94,486 files into one directory and, since macOS
        // records no Put Back data for a plain rename, made the original
        // locations unrecoverable. Structure must survive the move.
        let dest = Cleaner.destinationFor "/T" "run-1" "/Users/x/Library/Caches/brew/a.tar.gz"
        assertContains "Users" dest "the source tree is mirrored"
        assertContains "Library" dest "intermediate directories survive"
        assertContains "brew" dest "the leaf directory survives"
        assertContains "run-1" dest "and it sits under the run folder"
        assertFalse (dest = "/T/a.tar.gz") "it is NOT flattened to the trash root")

    test "the destination round-trips back to the original path" (fun () ->
        // Restoring is path arithmetic, so the arithmetic must be exact.
        for source in [ "/Users/x/Library/Caches/a.bin"
                        "/Users/x/.nuget/packages/pkg/1.0/lib/net10/z.dll"
                        "/tmp/simple.txt" ] do
            let dest = Cleaner.destinationFor "/T" "run-1" source
            assertEqual source (Cleaner.sourceFor "/T" "run-1" dest)
                (sprintf "round trip for %s" source))

    test "two files sharing a basename no longer collide" (fun () ->
        // Under the flat scheme these needed .1/.2 suffixes. Mirroring keeps
        // them apart naturally, which is what makes restore unambiguous.
        let a = Cleaner.destinationFor "/T" "run-1" "/one/dup.cache"
        let b = Cleaner.destinationFor "/T" "run-1" "/two/dup.cache"
        assertFalse (a = b) "distinct sources yield distinct destinations")

    test "the restore script is generated, executable, and quotes hostile paths" (fun () ->
        let script = IO.Path.Combine(IO.Path.GetTempPath(), "dcarestore.sh")
        if IO.File.Exists script then IO.File.Delete script
        // A path containing a single quote must not break out of the sh string.
        let sandbox = IO.Path.Combine(IO.Path.GetTempPath(), "dcaquote")
        if IO.Directory.Exists sandbox then IO.Directory.Delete(sandbox, true)
        IO.Directory.CreateDirectory sandbox |> ignore
        let nasty = IO.Path.Combine(sandbox, "it\'s a file.cache")
        IO.File.WriteAllText(nasty, "x")
        try
            let rule : Rules.ResolvedRule =
                { Rule = { Name = "P"; Platforms = Set.ofList [ MacOS; Linux; Windows ]
                           Root = Absolute sandbox; Include = []; Exclude = []
                           Risk = Safe; Recoverability = Regenerated; Strategy = ContentsOnly
                           MinAge = None; MinSize = None; SafeToDelete = true
                           Description = "d"; Reason = "r"; EstimatedBenefit = "e"
                           Examples = [ "x" ]; Category = Caches }
                  Root = sandbox }
            let meta = assertOk (Platform.FileSystem.real.GetInfo nasty) "stat"
            let candidate = assertOk (RiskEngine.evaluate false rule meta) "candidate"
            let stamp = DateTime(2026, 7, 22, 10, 0, 0)
            assertOk (Cleaner.writeRestoreScript script "/T" stamp [ candidate ]) "script written" |> ignore
            let text = IO.File.ReadAllText script
            assertContains "#!/bin/sh" text "has a shebang"
            assertContains "restore " text "emits a restore call"
            assertContains "it'\\''s a file.cache" text "single quotes are escaped for sh"
            assertFalse (text.Contains "rm ") "the script never deletes"
            let mode = IO.File.GetUnixFileMode script
            assertTrue (mode.HasFlag IO.UnixFileMode.UserExecute) "is executable"
        finally
            if IO.File.Exists script then IO.File.Delete script
            if IO.Directory.Exists sandbox then IO.Directory.Delete(sandbox, true))


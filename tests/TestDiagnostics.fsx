(**
    Tests for the Diagnostics module: counters, snapshots, and rendering.
*)
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

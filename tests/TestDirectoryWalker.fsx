(**
    Tests for DirectoryWalker: full traversal, symlink avoidance, depth
    limits, ignore paths, counters, and error collection under denial.
*)
open TestFramework
open Types
open Platform
open DirectoryWalker

let private tree =
    [ { Path = "/root"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
      { Path = "/root/a.log"; SizeBytes = 100L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0; ApparentBytes = None }
      { Path = "/root/sub"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
      { Path = "/root/sub/b.log"; SizeBytes = 200L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0; ApparentBytes = None }
      { Path = "/root/sub/deep"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
      { Path = "/root/sub/deep/c.log"; SizeBytes = 300L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0; ApparentBytes = None }
      { Path = "/root/ignored"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
      { Path = "/root/ignored/d.log"; SizeBytes = 400L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 5.0; ApparentBytes = None }
      { Path = "/root/link"; SizeBytes = 0L; IsDirectory = true; LinkTarget = Some "/elsewhere"; ModifiedDaysAgo = 1.0; ApparentBytes = None } ]

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

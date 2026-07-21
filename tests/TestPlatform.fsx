(**
    Tests for platform detection, PathSpec resolution, and the FileSystemOps
    abstraction — both the real implementation's shape and the in-memory
    implementation's fidelity, since every module above this one is tested
    against the in-memory implementation rather than the disk.
*)
open System
open TestFramework
open Types
open Platform

let run () =
    suite "Platform"

    test "current returns one of the three supported platforms" (fun () ->
        let p = current ()
        assertTrue (p = MacOS || p = Linux || p = Windows) "recognized platform")

    test "home resolves to an existing directory" (fun () ->
        let h = home ()
        assertTrue (h.Length > 0) "home is non-empty"
        assertTrue (IO.Directory.Exists h) "home exists")

    test "resolve expands a home-relative spec" (fun () ->
        match resolve (HomeRelative "Library/Caches") with
        | Some path ->
            assertTrue (path.StartsWith(home ())) "resolved under home"
            assertContains "Caches" path "retains the relative part"
        | None -> assertTrue false "HomeRelative should always resolve")

    test "resolve returns None for an unset environment variable" (fun () ->
        assertEqual None (resolve (EnvRelative("DEFINITELY_NOT_SET_XYZ", "sub"))) "unset variable yields None")

    test "resolve passes an absolute spec through unchanged" (fun () ->
        assertEqual (Some "/tmp") (resolve (Absolute "/tmp")) "absolute preserved")

    suite "Platform.FileSystem.inMemory"

    let tree =
        [ { Path = "/root"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
          { Path = "/root/a.log"; SizeBytes = 100L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 10.0 }
          { Path = "/root/b.txt"; SizeBytes = 200L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 2.0 }
          { Path = "/root/sub"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0 }
          { Path = "/root/sub/c.log"; SizeBytes = 300L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 90.0 }
          { Path = "/root/link"; SizeBytes = 0L; IsDirectory = false; LinkTarget = Some "/elsewhere"; ModifiedDaysAgo = 1.0 } ]

    let fs = FileSystem.inMemory tree

    test "enumerates immediate children only" (fun () ->
        let files = assertOk (fs.EnumerateFiles "/root") "enumerate /root files"
        assertEqual 3 files.Length "a.log, b.txt, link — not sub/c.log"
        let dirs = assertOk (fs.EnumerateDirectories "/root") "enumerate /root dirs"
        assertEqual [| "/root/sub" |] dirs "one subdirectory")

    test "reports metadata for a known file" (fun () ->
        let meta = assertOk (fs.GetInfo "/root/a.log") "get info"
        assertEqual 100L meta.SizeBytes "size"
        assertFalse meta.IsDirectory "not a directory"
        assertEqual None meta.LinkTarget "not a link")

    test "reports link targets so the walker can refuse to follow them" (fun () ->
        let meta = assertOk (fs.GetInfo "/root/link") "get link info"
        assertTrue (FileMeta.isLink meta) "recognized as a link")

    test "returns PathNotFound for an absent path" (fun () ->
        let err = assertError (fs.GetInfo "/root/nope") "absent path"
        assertEqual (PathNotFound "/root/nope") err "PathNotFound")

    test "modified times are back-dated so age rules can be tested" (fun () ->
        let meta = assertOk (fs.GetInfo "/root/sub/c.log") "get aged file"
        let age = FileMeta.age DateTime.UtcNow meta
        assertTrue (age.TotalDays > 89.0 && age.TotalDays < 91.0) "roughly 90 days old")

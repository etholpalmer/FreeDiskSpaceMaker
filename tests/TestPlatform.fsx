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

    test "the real filesystem enumerates hidden dot-directories" (fun () ->
        // .NET's EnumerationOptions defaults to AttributesToSkip = Hidden|System,
        // and on Unix every dotfile is Hidden. Under that default the walker
        // silently skipped ~/.nuget, ~/.npm, ~/.cache and friends — most of what
        // this tool exists to find. Measured: ~/.nuget/packages (5.2 GB) reported
        // zero findings until AttributesToSkip was set to None.
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "dcahidden")
        if IO.Directory.Exists root then IO.Directory.Delete(root, true)
        IO.Directory.CreateDirectory(IO.Path.Combine(root, ".hidden")) |> ignore
        IO.File.WriteAllText(IO.Path.Combine(root, ".hidden", "f.txt"), "x")
        try
            let dirs = assertOk (FileSystem.real.EnumerateDirectories root) "enumerate"
            assertTrue (dirs |> Array.exists (fun d -> d.EndsWith ".hidden"))
                "a dot-directory must be enumerated, not skipped as Hidden"
            let files = assertOk (FileSystem.real.EnumerateFiles (IO.Path.Combine(root, ".hidden"))) "files"
            assertEqual 1 files.Length "files inside a dot-directory are reachable"
        finally
            IO.Directory.Delete(root, true))

    test "an unreadable directory surfaces PermissionDenied, never a silent empty" (fun () ->
        // With EnumerationOptions.IgnoreInaccessible = true, .NET returns an empty
        // result for a directory it cannot read. The walker would then record no
        // error, unreadableDirectoryCount would stay zero, and the "grant Full Disk
        // Access" hint would never fire — a partial scan presented as a complete one.
        // This test fails if that flag is ever flipped back.
        let root = IO.Path.Combine(IO.Path.GetTempPath(), "dcadenied")
        let locked = IO.Path.Combine(root, "locked")
        let unlock () =
            try IO.File.SetUnixFileMode(locked,
                    IO.UnixFileMode.UserRead ||| IO.UnixFileMode.UserWrite ||| IO.UnixFileMode.UserExecute)
            with _ -> ()
        if IO.Directory.Exists root then
            unlock ()
            IO.Directory.Delete(root, true)
        IO.Directory.CreateDirectory locked |> ignore
        IO.File.WriteAllText(IO.Path.Combine(locked, "inner.txt"), "x")
        IO.File.WriteAllText(IO.Path.Combine(root, "visible.txt"), "y")
        try
            IO.File.SetUnixFileMode(locked, IO.UnixFileMode.None)
            match FileSystem.real.EnumerateFiles locked with
            | Ok entries ->
                assertTrue false
                    (sprintf "expected PermissionDenied, got Ok with %d entries — IgnoreInaccessible is true again" entries.Length)
            | Error (PermissionDenied _) -> ()
            | Error other -> assertTrue false (sprintf "expected PermissionDenied, got %A" other)
            // A readable sibling must still enumerate: the flag change must not
            // break partially-readable parents.
            let parentFiles = assertOk (FileSystem.real.EnumerateFiles root) "parent still readable"
            assertEqual 1 parentFiles.Length "parent yields its readable file despite a denied subdirectory"
        finally
            unlock ()
            IO.Directory.Delete(root, true))

    test "isElevated does not rely on the USER environment variable alone" (fun () ->
        // A bare $USER lookup is defeated by `env -u USER`. Environment.UserName
        // resolves from the effective uid on Unix, so it survives that. This test
        // pins the property rather than the implementation: under a normal
        // non-root session the answer must be false, and must stay false even
        // when USER is absent from the environment.
        assertFalse (isElevated ()) "a normal session is not elevated"
        let original = Environment.GetEnvironmentVariable "USER"
        try
            Environment.SetEnvironmentVariable("USER", null)
            assertFalse (isElevated ()) "still not elevated with USER unset"
            assertTrue (Environment.UserName.Length > 0)
                "Environment.UserName resolves without the USER variable"
        finally
            Environment.SetEnvironmentVariable("USER", original))


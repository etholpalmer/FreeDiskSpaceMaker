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
        [ { Path = "/root"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
          { Path = "/root/a.log"; SizeBytes = 100L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 10.0; ApparentBytes = None }
          { Path = "/root/b.txt"; SizeBytes = 200L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 2.0; ApparentBytes = None }
          { Path = "/root/sub"; SizeBytes = 0L; IsDirectory = true; LinkTarget = None; ModifiedDaysAgo = 1.0; ApparentBytes = None }
          { Path = "/root/sub/c.log"; SizeBytes = 300L; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = 90.0; ApparentBytes = None }
          { Path = "/root/link"; SizeBytes = 0L; IsDirectory = false; LinkTarget = Some "/elsewhere"; ModifiedDaysAgo = 1.0; ApparentBytes = None } ]

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

    // Ground truth for the two tests below, both of which behave differently
    // as root. Root ignores permission bits and is, by definition, elevated.
    // The dev machine is never root, so these assumptions were invisible until
    // the CI container (which runs as root) exercised them — the same
    // environment-privileged blindness the permission-denial fix was about,
    // now firing in the opposite direction. Derived from the effective uid via
    // Environment.UserName, not from $USER, so it is not fooled by the shell.
    let runningAsRoot =
        (current () = Types.MacOS || current () = Types.Linux)
        && Environment.UserName = "root"

    test "an unreadable directory surfaces PermissionDenied, never a silent empty" (fun () ->
        // With EnumerationOptions.IgnoreInaccessible = true, .NET returns an empty
        // result for a directory it cannot read. The walker would then record no
        // error, unreadableDirectoryCount would stay zero, and the "grant Full Disk
        // Access" hint would never fire — a partial scan presented as a complete one.
        // This test fails if that flag is ever flipped back.
        if runningAsRoot then
            // Root reads a chmod-000 directory regardless of its mode, so the
            // denial this test asserts genuinely cannot occur. Skipping is
            // correct, not a workaround: the invariant is real but unobservable
            // for this user. A non-root leg (macos-arm64) still enforces it.
            assertTrue true "skipped as root: permission bits do not deny root"
        else
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
        // resolves from the effective uid on Unix, so it survives that. The
        // property under test is not "always false" — it is "isElevated agrees
        // with whether the process is actually root, even when $USER is unset".
        // A non-root session expects false; a root container expects true.
        assertEqual runningAsRoot (isElevated ()) "isElevated reflects the real privilege level"
        let original = Environment.GetEnvironmentVariable "USER"
        try
            Environment.SetEnvironmentVariable("USER", null)
            assertEqual runningAsRoot (isElevated ())
                "the answer is unchanged when USER is removed from the environment"
            assertTrue (Environment.UserName.Length > 0)
                "Environment.UserName resolves without the USER variable"
        finally
            Environment.SetEnvironmentVariable("USER", original))

    test "a sparse file reports allocated bytes, not apparent length" (fun () ->
        // .NET exposes only FileInfo.Length, the logical extent. For a sparse
        // file that overstates disk use several-fold — measured on macOS,
        // Docker.raw showed 64.0 GB apparent against 16.7 GB allocated.
        // Every size the tool reports must be occupancy, not length.
        let path = IO.Path.Combine(IO.Path.GetTempPath(), "dcasparse.bin")
        if IO.File.Exists path then IO.File.Delete path
        try
            // 2 GiB logical, one byte written at the end: the middle is a hole.
            use fileStream = new IO.FileStream(path, IO.FileMode.CreateNew, IO.FileAccess.Write)
            fileStream.SetLength 2_147_483_648L
            fileStream.Seek(-1L, IO.SeekOrigin.End) |> ignore
            fileStream.WriteByte 1uy
            fileStream.Dispose()

            match FileSystem.allocatedBytes path with
            | None ->
                // stat unavailable — the fallback path. Not a failure, but the
                // sparse assertions below cannot be made, so state it.
                assertTrue true "allocation could not be measured on this platform"
            | Some allocated ->
                let meta = assertOk (FileSystem.real.GetInfo path) "stat the sparse file"
                assertEqual 2_147_483_648L meta.ApparentBytes "apparent length is the logical extent"
                assertEqual allocated meta.SizeBytes "reported size is the allocated size"
                assertTrue (meta.SizeBytes < meta.ApparentBytes) "allocation is below apparent length"
                assertTrue (FileMeta.isSparse meta) "recognised as sparse"
                assertTrue (FileMeta.sparseSavings meta > 0L) "savings are positive"
        finally
            if IO.File.Exists path then IO.File.Delete path)

    test "small files are not probed and report apparent length unchanged" (fun () ->
        // Probing costs a process spawn, so it must not happen per file across
        // a 200k-file scan. Below the threshold, size and apparent must agree.
        let path = IO.Path.Combine(IO.Path.GetTempPath(), "dcasmall.bin")
        IO.File.WriteAllText(path, String.replicate 1000 "x")
        try
            let meta = assertOk (FileSystem.real.GetInfo path) "stat a small file"
            assertEqual meta.ApparentBytes meta.SizeBytes "no probe, so both agree"
            assertFalse (FileMeta.isSparse meta) "not flagged sparse"
            assertTrue (meta.ApparentBytes < FileSystem.sparseProbeThresholdBytes) "and it is below the threshold"
        finally
            IO.File.Delete path)


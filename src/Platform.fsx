(**
    Platform detection, known-folder resolution, and the filesystem boundary.

    All filesystem access in this project goes through `FileSystemOps`, a record
    of functions rather than an interface. Two implementations exist: `real`,
    which touches the disk, and `inMemory`, which serves a synthetic tree. Every
    module above this one is written against the record, so the entire scanning
    and risk pipeline is testable without creating a single file.

    Every operation returns Result. Permission denial is the common case on
    macOS, not an edge case: TCC blocks ~/Library/Containers, ~/Library/Mail,
    and others unless the invoking terminal has Full Disk Access.
*)
module Platform

open System
open System.IO
open System.Runtime.InteropServices

open Types
open Utilities

/// The operating system this process is running on.
let current () : Types.Platform =
    if RuntimeInformation.IsOSPlatform OSPlatform.OSX then MacOS
    elif RuntimeInformation.IsOSPlatform OSPlatform.Windows then Windows
    else Linux

/// The invoking user's home directory.
let home () : string =
    Environment.GetFolderPath Environment.SpecialFolder.UserProfile

/// True when the process is running with elevated privileges.
///
/// The tool refuses to run elevated unless `--allow-root` is passed, because
/// an elevated scan can reach — and a bug could then remove — system files
/// that an ordinary user account is protected from.
let isElevated () : bool =
    match current () with
    | Windows ->
        // On Windows, an elevated process can write to the system directory.
        try
            let probe = Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.Windows, "elevation.probe")
            use _ = File.Create(probe, 1, FileOptions.DeleteOnClose)
            true
        with _ -> false
    | MacOS | Linux ->
        // This is a heuristic, not a real privilege check: obtaining the
        // effective uid would need a P/Invoke to getuid, and this project takes
        // no native dependencies. Three independent signals are used so that
        // defeating one is not enough.
        //
        // Environment.UserName is the strongest: on Unix .NET resolves it from
        // the effective uid rather than the environment, so unlike a bare
        // `$USER` lookup it survives `env -u USER` — verified on macOS, where
        // it still returned the real user with USER unset.
        //
        // The home directory catches a root shell that has been handed a
        // different name, and SUDO_USER catches `sudo` specifically.
        //
        // Even if all three are defeated, the RiskEngine denylist veto still
        // protects /System, /Library, /Applications and /etc regardless of
        // privilege — this check is a guard rail, not the guarantee.
        let home = Utilities.Path.normalize (Environment.GetFolderPath Environment.SpecialFolder.UserProfile)
        Environment.UserName = "root"
        || home = "/var/root"
        || not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable "SUDO_USER"))

/// Resolves a PathSpec against the current environment.
/// Returns None when an environment variable the spec depends on is unset,
/// which is how rules for absent tooling are dropped at startup.
let resolve (spec: PathSpec) : string option =
    match spec with
    | Absolute path -> Some (Utilities.Path.normalize path)
    | HomeRelative relative ->
        Some (Utilities.Path.normalize (Path.Combine(home (), relative)))
    | EnvRelative (variable, relative) ->
        match Environment.GetEnvironmentVariable variable with
        | null | "" -> None
        | value -> Some (Utilities.Path.normalize (Path.Combine(value, relative)))

/// The filesystem operations every other module depends on.
/// A record of functions rather than an interface, so substituting a fake in
/// tests is a value assignment rather than a type hierarchy.
type FileSystemOps =
    { /// Immediate subdirectories of a directory.
      EnumerateDirectories: string -> Result<string[], FsError>
      /// Immediate files within a directory.
      EnumerateFiles: string -> Result<string[], FsError>
      /// Metadata for a single entry.
      GetInfo: string -> Result<FileMeta, FsError>
      /// Opens a file for reading. The caller disposes the stream.
      OpenRead: string -> Result<Stream, FsError>
      /// True when the path exists as a file or directory.
      Exists: string -> bool
      /// The filesystem identity of a path, for boundary detection.
      VolumeOf: string -> Result<string, FsError> }

/// A synthetic filesystem entry for the in-memory implementation.
type TreeEntry =
    { Path: string
      SizeBytes: int64
      IsDirectory: bool
      LinkTarget: string option
      /// How long ago the entry was last modified, so age rules are testable.
      ModifiedDaysAgo: float
      /// Apparent length when it differs from allocation, so sparse-file
      /// handling is testable without creating a real sparse file.
      /// None means the entry is not sparse.
      ApparentBytes: int64 option }

module FileSystem =

    /// Converts a BCL exception into the corresponding FsError.
    let private classify (path: string) (ex: exn) : FsError =
        match ex with
        | :? UnauthorizedAccessException -> PermissionDenied path
        | :? DirectoryNotFoundException | :? FileNotFoundException -> PathNotFound path
        | :? PathTooLongException -> PathTooLong path
        | :? NotSupportedException as e -> NotSupported(path, e.Message)
        | e -> IoFailure(path, e.Message)

    /// Enumeration settings shared by the real implementation.
    ///
    /// IgnoreInaccessible must be false. With it true, enumerating an
    /// unreadable directory returns an empty result instead of throwing, so the
    /// walker records no error, `unreadableDirectoryCount` stays zero, and the
    /// "grant Full Disk Access" hint never fires — the tool presents a partial
    /// scan as a complete one. Measured: a chmod-000 directory returned
    /// `Ok, 0 entries` and its parent walk reported 0 errors.
    ///
    /// Setting it false costs nothing here, because the walker descends
    /// manually with RecurseSubdirectories = false, so each call lists exactly
    /// one directory. On Unix, listing a directory needs read+execute on that
    /// directory alone, not on its children. Measured on a parent holding an
    /// unreadable file and a chmod-000 subdirectory: GetFiles returned all 3
    /// readable entries and GetDirectories all 2, identical to IgnoreInaccessible
    /// = true. Only enumerating the denied directory itself throws — which is
    /// exactly the case that must be surfaced. The partial-read protection this
    /// flag offers applies to recursive enumeration, which this code never uses.
    ///
    /// AttributesToSkip must be None. The .NET default is `Hidden ||| System`,
    /// and on Unix every dotfile carries the Hidden attribute — so the default
    /// silently skips `.nuget`, `.npm`, `.cache`, `.gradle`, `.m2`, `.yarn`,
    /// `.local` and `.Trash`, which is most of what this tool exists to find.
    /// Measured on macOS: the default returned 53 of 118 directories in a home
    /// directory, and `~/.nuget/packages` (5.2 GB) reported zero findings.
    let private enumerationOptions =
        EnumerationOptions(
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.None)

    /// Files at or above this apparent size are measured for real allocation.
    ///
    /// Measuring costs a `stat` process spawn — about 4 ms — so it is not worth
    /// doing per file across a 200k-file scan. Sparseness only misleads when
    /// the gap is large, and a gap that matters implies a large file, so a size
    /// floor is the right filter. Extension is not: measured on macOS, `.raw`
    /// images were sparse while `.qcow2`, `.hds` and `.iso` files were not, so
    /// the filename predicts nothing.
    let sparseProbeThresholdBytes = 1_073_741_824L // 1 GiB

    /// Bytes actually allocated to a file, or None when it cannot be measured.
    ///
    /// .NET exposes no allocated-size API — `FileInfo.Length` is the logical
    /// extent — so this shells out to `stat`, which reports 512-byte blocks.
    /// Returns None on Windows, when `stat` is absent, or on any failure, and
    /// callers then fall back to the apparent size. Being wrong here must
    /// degrade to today's behaviour, never to an error.
    let allocatedBytes (path: string) : int64 option =
        let arguments =
            match current () with
            | MacOS -> Some [| "-f"; "%b"; path |]
            | Linux -> Some [| "-c"; "%b"; path |]
            | Windows -> None // NTFS sparse queries need P/Invoke; out of scope.

        match arguments with
        | None -> None
        | Some args ->
            try
                let startInfo = Diagnostics.ProcessStartInfo(FileName = "stat", RedirectStandardOutput = true,
                                                             RedirectStandardError = true, UseShellExecute = false)
                for a in args do startInfo.ArgumentList.Add a
                use process' = Diagnostics.Process.Start startInfo
                let output = process'.StandardOutput.ReadToEnd()
                process'.WaitForExit()
                if process'.ExitCode <> 0 then None
                else
                    match Int64.TryParse(output.Trim()) with
                    | true, blocks when blocks >= 0L -> Some (blocks * 512L)
                    | _ -> None
            with _ -> None

    /// The real filesystem.
    let real: FileSystemOps =
        { EnumerateDirectories = fun path ->
            try Ok (Directory.GetDirectories(path, "*", enumerationOptions))
            with ex -> Error (classify path ex)

          EnumerateFiles = fun path ->
            try Ok (Directory.GetFiles(path, "*", enumerationOptions))
            with ex -> Error (classify path ex)

          GetInfo = fun path ->
            try
                let isDirectory = Directory.Exists path
                if not isDirectory && not (File.Exists path) then Error (PathNotFound path)
                else
                    let info: FileSystemInfo =
                        if isDirectory then DirectoryInfo path :> FileSystemInfo
                        else FileInfo path :> FileSystemInfo
                    let apparent =
                        match info with
                        | :? FileInfo as f -> f.Length
                        | _ -> 0L
                    // Only large files are measured; see sparseProbeThresholdBytes.
                    // A failed measurement falls back to the apparent size, so
                    // this can only improve accuracy, never break the scan.
                    let size =
                        if isDirectory || apparent < sparseProbeThresholdBytes then apparent
                        else allocatedBytes path |> Option.defaultValue apparent
                    let mode =
                        if current () = Windows then ""
                        else
                            try string (File.GetUnixFileMode path) with _ -> ""
                    Ok { Path = path
                         SizeBytes = size
                         ApparentBytes = apparent
                         LastAccess = info.LastAccessTimeUtc
                         LastModified = info.LastWriteTimeUtc
                         Created = info.CreationTimeUtc
                         IsDirectory = isDirectory
                         LinkTarget = (if isNull info.LinkTarget then None else Some info.LinkTarget)
                         Owner = Environment.UserName
                         Mode = mode }
            with ex -> Error (classify path ex)

          OpenRead = fun path ->
            try Ok (File.OpenRead path :> Stream)
            with ex -> Error (classify path ex)

          Exists = fun path -> File.Exists path || Directory.Exists path

          VolumeOf = fun path ->
            try
                let root = Path.GetPathRoot(Path.GetFullPath path)
                Ok (DriveInfo(root).Name)
            with ex -> Error (classify path ex) }

    /// A synthetic filesystem backed by a list of entries.
    /// Paths are matched after normalization, so tests may write them in any
    /// separator style.
    let inMemory (entries: TreeEntry list) : FileSystemOps =
        let now = DateTime.UtcNow
        let byPath =
            entries
            |> List.map (fun e -> Utilities.Path.normalize e.Path, e)
            |> Map.ofList

        let toMeta (e: TreeEntry) : FileMeta =
            let modified = now.AddDays(-e.ModifiedDaysAgo)
            { Path = e.Path
              SizeBytes = e.SizeBytes
              ApparentBytes = defaultArg e.ApparentBytes e.SizeBytes
              LastAccess = modified
              LastModified = modified
              Created = modified
              IsDirectory = e.IsDirectory
              LinkTarget = e.LinkTarget
              Owner = "testuser"
              Mode = "rw-r--r--" }

        /// Immediate children of a directory: descendants with exactly one
        /// additional path segment.
        let childrenOf (parent: string) =
            let p = Utilities.Path.normalize parent
            let depth = (Utilities.Path.segments p).Length
            byPath
            |> Map.toList
            |> List.map snd
            |> List.filter (fun e ->
                Utilities.Path.isDescendantOf p e.Path
                && (Utilities.Path.segments (Utilities.Path.normalize e.Path)).Length = depth + 1)

        { EnumerateDirectories = fun path ->
            if not (byPath.ContainsKey(Utilities.Path.normalize path)) then Error (PathNotFound path)
            else Ok (childrenOf path |> List.filter (fun e -> e.IsDirectory)
                                     |> List.map (fun e -> e.Path) |> List.toArray)

          EnumerateFiles = fun path ->
            if not (byPath.ContainsKey(Utilities.Path.normalize path)) then Error (PathNotFound path)
            else Ok (childrenOf path |> List.filter (fun e -> not e.IsDirectory)
                                     |> List.map (fun e -> e.Path) |> List.toArray)

          GetInfo = fun path ->
            match byPath.TryFind(Utilities.Path.normalize path) with
            | Some e -> Ok (toMeta e)
            | None -> Error (PathNotFound path)

          OpenRead = fun path ->
            match byPath.TryFind(Utilities.Path.normalize path) with
            | Some e when not e.IsDirectory ->
                // Deterministic synthetic content sized to match the entry, so
                // hashing tests produce stable results.
                Ok (new MemoryStream(Array.create (int e.SizeBytes) 0uy) :> Stream)
            | Some _ -> Error (NotSupported(path, "cannot read a directory"))
            | None -> Error (PathNotFound path)

          Exists = fun path -> byPath.ContainsKey(Utilities.Path.normalize path)

          VolumeOf = fun _ -> Ok "/testvolume" }

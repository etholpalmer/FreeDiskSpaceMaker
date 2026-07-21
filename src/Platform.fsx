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
        // getuid() returns 0 for root. SUDO_USER additionally catches `sudo`.
        Environment.GetEnvironmentVariable "USER" = "root"
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
      ModifiedDaysAgo: float }

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
    /// IgnoreInaccessible lets a partially-readable directory still yield its
    /// readable entries instead of failing wholesale.
    ///
    /// AttributesToSkip must be None. The .NET default is `Hidden ||| System`,
    /// and on Unix every dotfile carries the Hidden attribute — so the default
    /// silently skips `.nuget`, `.npm`, `.cache`, `.gradle`, `.m2`, `.yarn`,
    /// `.local` and `.Trash`, which is most of what this tool exists to find.
    /// Measured on macOS: the default returned 53 of 118 directories in a home
    /// directory, and `~/.nuget/packages` (5.2 GB) reported zero findings.
    let private enumerationOptions =
        EnumerationOptions(
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.None)

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
                    let size =
                        match info with
                        | :? FileInfo as f -> f.Length
                        | _ -> 0L
                    let mode =
                        if current () = Windows then ""
                        else
                            try string (File.GetUnixFileMode path) with _ -> ""
                    Ok { Path = path
                         SizeBytes = size
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

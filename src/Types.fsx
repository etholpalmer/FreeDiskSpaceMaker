(**
    Core domain types for DiskCleanupAdvisor.

    This module contains only type definitions and total, side-effect-free
    functions over them. It has no dependencies, sits at the root of the
    #load graph, and must never acquire one.
*)
module Types

open System

/// The operating systems this tool supports.
type Platform =
    | MacOS
    | Linux
    | Windows

/// How dangerous it is to delete a given item.
/// `NeverDelete` is a veto that no command-line flag can override.
type RiskLevel =
    | Safe
    | ProbablySafe
    | Review
    | Dangerous
    | NeverDelete

/// Presentation metadata for a risk level. Kept out of the DU itself so
/// `RiskLevel` stays comparable and cheap to pattern-match.
type RiskDescription =
    { Icon: string
      AnsiColor: string
      Description: string }

module RiskLevel =
    /// Returns the icon, color, and human-readable meaning of a risk level.
    let describe (level: RiskLevel) : RiskDescription =
        match level with
        | Safe ->
            { Icon = "✓"; AnsiColor = "\027[32m"
              Description = "Regenerable. No user data. Safe to remove." }
        | ProbablySafe ->
            { Icon = "○"; AnsiColor = "\027[36m"
              Description = "Regenerable, but an application may need to re-download it." }
        | Review ->
            { Icon = "?"; AnsiColor = "\027[33m"
              Description = "Requires human judgment. Not deleted automatically." }
        | Dangerous ->
            { Icon = "!"; AnsiColor = "\027[35m"
              Description = "Deletion is likely to break something." }
        | NeverDelete ->
            { Icon = "✕"; AnsiColor = "\027[31m"
              Description = "Protected. This tool will never remove it." }

    /// Orders risk levels from least (0) to most (4) dangerous.
    let severity (level: RiskLevel) : int =
        match level with
        | Safe -> 0
        | ProbablySafe -> 1
        | Review -> 2
        | Dangerous -> 3
        | NeverDelete -> 4

    /// Parses a risk level from configuration text. Case-insensitive.
    let parse (text: string) : Result<RiskLevel, string> =
        match text.Trim().ToLowerInvariant() with
        | "safe" -> Ok Safe
        | "probablysafe" | "probably-safe" -> Ok ProbablySafe
        | "review" -> Ok Review
        | "dangerous" -> Ok Dangerous
        | "neverdelete" | "never-delete" -> Ok NeverDelete
        | other -> Error (sprintf "unknown risk level '%s'" other)

    /// Renders a risk level as stable text for reports and JSON.
    let toText (level: RiskLevel) : string =
        match level with
        | Safe -> "Safe"
        | ProbablySafe -> "ProbablySafe"
        | Review -> "Review"
        | Dangerous -> "Dangerous"
        | NeverDelete -> "NeverDelete"

/// How an item can be recovered after removal.
type Recoverability =
    | Regenerated
    | Redownloadable
    | RecoverableFromTrash
    | Unrecoverable

module Recoverability =
    /// Renders recoverability as stable text for reports and JSON.
    let toText (r: Recoverability) : string =
        match r with
        | Regenerated -> "Regenerated"
        | Redownloadable -> "Redownloadable"
        | RecoverableFromTrash -> "RecoverableFromTrash"
        | Unrecoverable -> "Unrecoverable"

/// How a rule's matched items should be removed.
type DeleteStrategy =
    /// Empty the directory but keep the directory itself (tools may expect it).
    | ContentsOnly
    /// Remove only files matching the rule's include globs.
    | MatchingFiles
    /// Remove the directory itself; it is fully regenerable.
    | WholeDirectory

module DeleteStrategy =
    /// Renders a delete strategy as stable text for reports and JSON.
    let toText (s: DeleteStrategy) : string =
        match s with
        | ContentsOnly -> "ContentsOnly"
        | MatchingFiles -> "MatchingFiles"
        | WholeDirectory -> "WholeDirectory"

/// An unresolved location, resolved against the environment at startup.
type PathSpec =
    | HomeRelative of string
    | Absolute of string
    | EnvRelative of variable: string * relative: string

/// What kind of thing an item is, for grouping in reports.
type Category =
    | Caches
    | Logs
    | TemporaryFiles
    | CrashReports
    | DeveloperArtifacts
    | PackageManagerCache
    | BrowserCache
    | ContainerData
    | VirtualMachines
    | Archives
    | DiskImages
    | Installers
    | LargeFiles
    | DuplicateFiles
    | EmptyDirectories
    | Applications
    | Downloads
    | TrashContents
    | Snapshots
    | Other

module Category =
    /// Renders a category as a human-readable heading.
    let toText (c: Category) : string =
        match c with
        | Caches -> "Caches"
        | Logs -> "Logs"
        | TemporaryFiles -> "Temporary Files"
        | CrashReports -> "Crash Reports"
        | DeveloperArtifacts -> "Developer Artifacts"
        | PackageManagerCache -> "Package Manager Caches"
        | BrowserCache -> "Browser Caches"
        | ContainerData -> "Container Data"
        | VirtualMachines -> "Virtual Machines"
        | Archives -> "Archives"
        | DiskImages -> "Disk Images"
        | Installers -> "Installers"
        | LargeFiles -> "Large Files"
        | DuplicateFiles -> "Duplicate Files"
        | EmptyDirectories -> "Empty Directories"
        | Applications -> "Applications"
        | Downloads -> "Downloads"
        | TrashContents -> "Trash"
        | Snapshots -> "Local Snapshots"
        | Other -> "Other"

    /// Every category, for report iteration and test coverage.
    let all =
        [ Caches; Logs; TemporaryFiles; CrashReports; DeveloperArtifacts
          PackageManagerCache; BrowserCache; ContainerData; VirtualMachines
          Archives; DiskImages; Installers; LargeFiles; DuplicateFiles
          EmptyDirectories; Applications; Downloads; TrashContents; Snapshots; Other ]

/// Why a filesystem operation failed. Collected, never thrown.
type FsError =
    | PermissionDenied of path: string
    | PathNotFound of path: string
    | PathTooLong of path: string
    | IoFailure of path: string * message: string
    | NotSupported of path: string * message: string

module FsError =
    /// The path an error concerns.
    let path (e: FsError) : string =
        match e with
        | PermissionDenied p | PathNotFound p | PathTooLong p -> p
        | IoFailure (p, _) | NotSupported (p, _) -> p

    /// A one-line human-readable rendering.
    let toText (e: FsError) : string =
        match e with
        | PermissionDenied p -> sprintf "permission denied: %s" p
        | PathNotFound p -> sprintf "not found: %s" p
        | PathTooLong p -> sprintf "path too long: %s" p
        | IoFailure (p, m) -> sprintf "I/O error at %s: %s" p m
        | NotSupported (p, m) -> sprintf "unsupported at %s: %s" p m

/// Metadata about a single filesystem entry, captured during traversal.
type FileMeta =
    { Path: string
      SizeBytes: int64
      LastAccess: DateTime
      LastModified: DateTime
      Created: DateTime
      IsDirectory: bool
      /// Non-None when the entry is a symlink or reparse point. Never followed.
      LinkTarget: string option
      Owner: string
      /// Unix mode bits rendered as text (e.g. "rw-r--r--"). Empty on Windows.
      Mode: string }

module FileMeta =
    /// Age since last modification, relative to the supplied clock.
    let age (now: DateTime) (m: FileMeta) : TimeSpan = now - m.LastModified

    /// True when the entry is a link and must not be traversed or deleted.
    let isLink (m: FileMeta) : bool = m.LinkTarget.IsSome

(**
    Classifies a path into a report category.

    Directory context is checked before file extension, because the enclosing
    directory is the stronger signal about regenerability: a `.log` inside
    ~/Library/Caches is cache that will be regenerated, not a log worth keeping.

    Classification is presentation only. It groups findings in reports and has
    no bearing on whether anything may be deleted — that is entirely the risk
    engine's decision.
*)
module FileClassifier

open System

open Types
open Utilities

/// Directory names that determine a category regardless of file extension.
/// Ordered most specific first.
let private decisiveDirectories =
    [ "DerivedData", DeveloperArtifacts
      "node_modules", DeveloperArtifacts
      "__pycache__", DeveloperArtifacts
      "CoreSimulator", DeveloperArtifacts
      "CrashReporter", CrashReports
      "DiagnosticReports", CrashReports
      "Caches", Caches
      "_cacache", PackageManagerCache
      "Logs", Logs ]

/// Extension groups. Compound extensions such as `.tar.gz` are handled by
/// suffix matching rather than by Path.GetExtension, which returns only `.gz`.
let private extensionGroups =
    [ [ ".zip"; ".tar"; ".tar.gz"; ".tgz"; ".tar.bz2"; ".rar"; ".7z"; ".bz2"; ".xz" ], Archives
      [ ".dmg"; ".iso"; ".img"; ".vmdk"; ".qcow2"; ".vdi"; ".sparseimage" ], DiskImages
      [ ".pkg"; ".msi"; ".deb"; ".rpm"; ".exe" ], Installers
      [ ".crash"; ".ips"; ".diag"; ".panic" ], CrashReports
      [ ".tmp"; ".temp"; ".swp"; ".swo"; ".partial"; ".download" ], TemporaryFiles
      [ ".log"; ".log.gz" ], Logs
      [ ".o"; ".obj"; ".pyc"; ".class"; ".dSYM" ], DeveloperArtifacts ]

/// True when the lowercase filename ends with any extension in the list.
let private hasExtension (extensions: string list) (lowerName: string) : bool =
    extensions |> List.exists (fun e -> lowerName.EndsWith(e, StringComparison.Ordinal))

/// True when a filename is a rotated log such as `app.log.1`.
let private isRotatedLog (lowerName: string) : bool =
    let parts = lowerName.Split '.'
    parts.Length >= 3
    && parts.[parts.Length - 2] = "log"
    && parts.[parts.Length - 1] |> Seq.forall Char.IsDigit

/// Assigns a report category to a path.
let classify (path: string) : Category =
    let normalized = Utilities.Path.normalize path
    let segments = Utilities.Path.segments normalized
    let lowerName =
        match List.tryLast segments with
        | Some name -> name.ToLowerInvariant()
        | None -> ""

    // Directory context first — it is the stronger signal.
    let fromDirectory =
        decisiveDirectories
        |> List.tryPick (fun (directoryName, category) ->
            if segments |> List.exists (fun s -> String.Equals(s, directoryName, StringComparison.OrdinalIgnoreCase))
            then Some category
            else None)

    match fromDirectory with
    | Some category -> category
    | None ->
        if isRotatedLog lowerName then Logs
        else
            extensionGroups
            |> List.tryPick (fun (extensions, category) ->
                if hasExtension extensions lowerName then Some category else None)
            |> Option.defaultValue Other

/// True when the path names a compressed archive.
let isArchive (path: string) : bool = classify path = Archives

/// True when the path names a disk or virtual-machine image.
let isDiskImage (path: string) : bool = classify path = DiskImages

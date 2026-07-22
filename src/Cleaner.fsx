(**
    Moves items to the platform Trash.

    **This module contains no File.Delete, Directory.Delete, or any other
    unlink call, and must never acquire one.** Every removal is a relocation
    into the Trash, which the user empties themselves. That is what makes every
    action this tool takes reversible.

    A consequence worth stating in reports: space is not reclaimed at the moment
    this module runs. It is reclaimed when the user empties the Trash.

    Deletions are journaled before the move, not after, so a crash mid-run
    leaves an accurate record of what was attempted.
*)
module Cleaner

open System
open System.IO
open System.Text.Json

/// What a trash operation accomplished.
type TrashResult =
    { Moved: string list
      Failed: (string * Types.FsError) list
      BytesMoved: int64 }

/// One journal entry, written before its move is attempted.
///
/// Must NOT be `private`. System.Text.Json serializes only public properties,
/// and an F# private record type exposes none — every entry then serializes as
/// `{}`. This shipped: a real run journaled 94,488 empty objects, leaving no
/// audit trail for the 15.2 GB it had just moved.
type JournalEntry =
    { Path: string
      RuleName: string
      SizeBytes: int64
      AttemptedAtUtc: string }

/// Resolves the platform Trash directory.
let trashRoot () : Result<string, string> =
    match Platform.current () with
    | Types.MacOS ->
        Ok (Path.Combine(Platform.home (), ".Trash"))
    | Types.Linux ->
        // XDG Trash specification.
        let dataHome =
            match Environment.GetEnvironmentVariable "XDG_DATA_HOME" with
            | null | "" -> Path.Combine(Platform.home (), ".local", "share")
            | value -> value
        Ok (Path.Combine(dataHome, "Trash", "files"))
    | Types.Windows ->
        // Moving to the Recycle Bin requires SHFileOperation, which this
        // script-only project cannot P/Invoke portably. Refuse rather than
        // silently unlinking — an irreversible fallback would defeat the
        // entire safety model.
        Error "moving to the Recycle Bin is not supported on Windows in this version; run without --delete"

/// Where the deletion journal for a run is written.
let journalPath (timestamp: DateTime) : string =
    let name = sprintf "deletions-%s.json" (timestamp.ToString "yyyy-MM-dd-HHmmss")
    Path.Combine("reports", name)

/// Chooses a destination filename that does not already exist.
///
/// `exists` is passed in rather than called directly so the collision logic
/// can be tested without touching the filesystem.
let uniqueDestination (exists: string -> bool) (directory: string) (fileName: string) : string =
    let candidate = Path.Combine(directory, fileName)
    if not (exists candidate) then candidate
    else
        // Appended after the full name (including its extension), e.g.
        // "a.txt" -> "a.txt.1", rather than inserted before the extension —
        // that keeps the original name intact and recognizable in the Trash.
        let rec attempt (n: int) =
            let next = Path.Combine(directory, sprintf "%s.%d" fileName n)
            if exists next then attempt (n + 1) else next
        attempt 1

/// Writes the journal for a set of candidates. Called before any move.
let writeJournal (path: string) (candidates: RiskEngine.DeletionCandidate list) : Result<unit, string> =
    try
        let entries =
            candidates
            |> List.map (fun c ->
                { Path = RiskEngine.candidatePath c
                  RuleName = RiskEngine.candidateRule c
                  SizeBytes = RiskEngine.candidateSize c
                  AttemptedAtUtc = DateTime.UtcNow.ToString "o" })
        let directory = Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (Directory.Exists directory) then
            Directory.CreateDirectory directory |> ignore
        File.WriteAllText(path, JsonSerializer.Serialize(entries, Dto.serializerOptions))
        Ok ()
    with ex -> Error (sprintf "cannot write the deletion journal to %s: %s" path ex.Message)

/// Moves every candidate into the Trash.
///
/// Each item is re-checked immediately before its move: if it changed since
/// the scan, it is skipped rather than moved, because the thing being removed
/// is no longer the thing that was evaluated.
let moveToTrash
    (fs: Platform.FileSystemOps)
    (log: Logging.Logger)
    (candidates: RiskEngine.DeletionCandidate list)
    : TrashResult =

    match trashRoot () with
    | Error message ->
        log |> Logging.error message
        { Moved = []
          Failed = candidates |> List.map (fun c -> RiskEngine.candidatePath c, Types.NotSupported(RiskEngine.candidatePath c, message))
          BytesMoved = 0L }
    | Ok trash ->

    // Creating the Trash directory can fail — a read-only home, or a
    // permission failure on ~/.Trash. This runs after the journal was written
    // and after the user confirmed, so an escaping exception would replace the
    // promised summary with a stack trace. Report it as every candidate having
    // failed, which is the truth: nothing moved.
    let trashReady =
        try
            if not (Directory.Exists trash) then Directory.CreateDirectory trash |> ignore
            Ok ()
        with ex -> Error (sprintf "cannot prepare the Trash directory %s: %s" trash ex.Message)

    match trashReady with
    | Error message ->
        log |> Logging.error message
        { Moved = []
          Failed =
            candidates
            |> List.map (fun c ->
                let path = RiskEngine.candidatePath c
                path, Types.IoFailure(path, message))
          BytesMoved = 0L }
    | Ok () ->

    let moved = ResizeArray<string>()
    let failed = ResizeArray<string * Types.FsError>()
    let mutable bytesMoved = 0L

    for candidate in candidates do
        let source = RiskEngine.candidatePath candidate
        let expectedSize = RiskEngine.candidateSize candidate

        // Re-stat before moving. A file that changed since the scan was not
        // the file the risk engine approved.
        match fs.GetInfo source with
        | Error e ->
            failed.Add(source, e)
        | Ok current when current.SizeBytes <> expectedSize ->
            log |> Logging.warn (sprintf "skipping %s: changed since the scan" source)
            failed.Add(source, Types.IoFailure(source, "changed since the scan"))
        | Ok _ ->
            try
                // Choosing a destination probes the filesystem, so it belongs
                // inside the guard: a permission failure here would otherwise
                // escape and abandon the remaining candidates unreported.
                let destination =
                    uniqueDestination (fun p -> File.Exists p || Directory.Exists p) trash (Path.GetFileName source)
                // Move, never delete. On a cross-device move File.Move copies
                // then removes the source, which is still a relocation.
                if Directory.Exists source then Directory.Move(source, destination)
                else File.Move(source, destination)
                moved.Add source
                bytesMoved <- bytesMoved + expectedSize
                log |> Logging.debug (sprintf "moved %s to %s" source destination)
            with ex ->
                log |> Logging.warn (sprintf "could not move %s: %s" source ex.Message)
                failed.Add(source, Types.IoFailure(source, ex.Message))

    { Moved = List.ofSeq moved
      Failed = List.ofSeq failed
      BytesMoved = bytesMoved }

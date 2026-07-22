(**
    Duplicate detection via a three-stage funnel.

      1. Group by exact byte size — stat only, no reads. Singletons drop out,
         which eliminates roughly 95% of candidates on a real machine.
      2. Sampled hash on survivors — two reads per file regardless of size.
      3. Full hash only where the sampled hash still collides.

    Duplicates are reported at risk level Review and are never Safe. Identical
    bytes do not make a copy disposable: the second copy may be the one a
    running program references, or the one inside a backup the user relies on.
*)
module Duplicates

open System

/// A set of files with identical content.
type DuplicateGroup =
    { Hash: string
      SizeBytes: int64
      Paths: string list
      /// The copy to keep: oldest first, then shallowest, then alphabetical.
      Keeper: string
      /// Bytes consumed by every copy except the keeper.
      Wasted: int64 }

/// Chooses which copy to keep.
///
/// Oldest wins, on the reasoning that the earliest copy is most likely the
/// original. Ties break toward the shallowest path, then alphabetically, so
/// the choice is deterministic across runs.
let private chooseKeeper (metas: Types.FileMeta list) : string =
    metas
    |> List.sortBy (fun m ->
        m.LastModified,
        (Utilities.Path.segments m.Path).Length,
        m.Path)
    |> List.head
    |> fun m -> m.Path

/// Groups a list of items by a fallible key, discarding items whose key
/// could not be computed and groups with only one member.
let private groupByHash
    (computeKey: Types.FileMeta -> Result<string, Types.FsError>)
    (items: Types.FileMeta list)
    : (string * Types.FileMeta list) list =
    items
    |> List.choose (fun meta ->
        match computeKey meta with
        | Ok key -> Some (key, meta)
        | Error _ -> None)          // unreadable files simply are not compared
    |> List.groupBy fst
    |> List.map (fun (key, pairs) -> key, pairs |> List.map snd)
    |> List.filter (fun (_, group) -> group.Length > 1)

/// Finds groups of byte-identical files.
let find
    (fs: Platform.FileSystemOps)
    (algorithm: Hashing.Algorithm)
    (minSizeBytes: int64)
    (files: Types.FileMeta list)
    : DuplicateGroup list =

    files
    // Stage 1 — exact size. Free: no file is opened.
    |> List.filter (fun m -> not m.IsDirectory && m.SizeBytes >= minSizeBytes)
    |> List.groupBy (fun m -> m.SizeBytes)
    |> List.filter (fun (_, group) -> group.Length > 1)
    |> List.collect (fun (size, sameSize) ->
        // Stage 2 — sampled hash. Two reads per file.
        sameSize
        |> groupByHash (fun m -> Hashing.sampled fs m.Path m.SizeBytes)
        |> List.collect (fun (_, sampledMatch) ->
            // Stage 3 — full hash, only for files that survived stage 2.
            sampledMatch
            |> groupByHash (fun m -> Hashing.full fs algorithm m.Path)
            |> List.map (fun (hash, confirmed) ->
                { Hash = hash
                  SizeBytes = size
                  Paths = confirmed |> List.map (fun m -> m.Path) |> List.sort
                  Keeper = chooseKeeper confirmed
                  Wasted = size * int64 (confirmed.Length - 1) })))
    |> List.sortByDescending (fun g -> g.Wasted)

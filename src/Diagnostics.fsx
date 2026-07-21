(**
    Traversal counters and throughput reporting.

    This module is the one deliberate exception to the project's immutability
    rule. Counters are incremented once per filesystem entry from multiple
    threads; allocating a new record per increment would dominate the cost of
    the scan itself. Updates use Interlocked so the totals stay correct under
    parallel traversal.
*)
module Diagnostics

open System
open System.Diagnostics
open System.Threading

open Utilities

/// Why a directory or file was not examined.
type SkipReason =
    | Permission
    | Symlink
    | FilesystemBoundary
    | DepthLimit
    | Excluded

module SkipReason =
    /// Human-readable label for reports.
    let toText (r: SkipReason) : string =
        match r with
        | Permission -> "permission denied"
        | Symlink -> "symlink"
        | FilesystemBoundary -> "different filesystem"
        | DepthLimit -> "depth limit"
        | Excluded -> "excluded by config"

    /// Every reason, so reports can show zero counts explicitly.
    let all = [ Permission; Symlink; FilesystemBoundary; DepthLimit; Excluded ]

/// Live, thread-safe traversal counters.
type Counters =
    { Clock: Stopwatch
      mutable Directories: int64
      mutable Files: int64
      mutable Bytes: int64
      Skips: Collections.Concurrent.ConcurrentDictionary<SkipReason, int64 ref> }

/// An immutable point-in-time reading of the counters.
type Snapshot =
    { Elapsed: TimeSpan
      Directories: int64
      Files: int64
      Bytes: int64
      SkipsByReason: Map<SkipReason, int64>
      PeakMemoryBytes: int64
      DirectoriesPerSecond: float
      FilesPerSecond: float
      BytesPerSecond: float }

/// Creates a fresh counter set with its clock already running.
let create () : Counters =
    { Clock = Stopwatch.StartNew()
      Directories = 0L
      Files = 0L
      Bytes = 0L
      Skips = Collections.Concurrent.ConcurrentDictionary() }

/// Records that one directory was examined.
let recordDirectory (c: Counters) : unit =
    Interlocked.Increment(&c.Directories) |> ignore

/// Records that one file of the given size was examined.
let recordFile (c: Counters) (sizeBytes: int64) : unit =
    Interlocked.Increment(&c.Files) |> ignore
    Interlocked.Add(&c.Bytes, sizeBytes) |> ignore

/// Records that one entry was skipped for the given reason.
let recordSkip (c: Counters) (reason: SkipReason) : unit =
    let cell = c.Skips.GetOrAdd(reason, fun _ -> ref 0L)
    Interlocked.Increment cell |> ignore

/// Takes an immutable reading of the current counters.
let snapshot (c: Counters) : Snapshot =
    let elapsed = c.Clock.Elapsed
    let seconds = max elapsed.TotalSeconds 0.001
    let directories = Interlocked.Read &c.Directories
    let files = Interlocked.Read &c.Files
    let bytes = Interlocked.Read &c.Bytes
    let skips =
        SkipReason.all
        |> List.map (fun r ->
            match c.Skips.TryGetValue r with
            | true, cell -> r, cell.Value
            | _ -> r, 0L)
        |> Map.ofList
    { Elapsed = elapsed
      Directories = directories
      Files = files
      Bytes = bytes
      SkipsByReason = skips
      PeakMemoryBytes = GC.GetTotalMemory false
      DirectoriesPerSecond = float directories / seconds
      FilesPerSecond = float files / seconds
      BytesPerSecond = float bytes / seconds }

/// Renders a snapshot as an aligned multi-line block for the console report.
let render (s: Snapshot) : string =
    let lines =
        [ sprintf "  Elapsed         %s" (Format.duration s.Elapsed)
          sprintf "  Directories     %d  (%s)" s.Directories (Format.rate s.DirectoriesPerSecond "dir")
          sprintf "  Files           %d  (%s)" s.Files (Format.rate s.FilesPerSecond "file")
          sprintf "  Examined        %s  (%s)" (Format.bytes s.Bytes) (Format.rate s.BytesPerSecond "B")
          sprintf "  Memory          %s" (Format.bytes s.PeakMemoryBytes) ]
    let skipLines =
        s.SkipsByReason
        |> Map.toList
        |> List.filter (fun (_, count) -> count > 0L)
        |> List.map (fun (reason, count) -> sprintf "  Skipped         %d (%s)" count (SkipReason.toText reason))
    String.Join("\n", lines @ skipLines)

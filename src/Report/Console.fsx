(**
    Console report — the primary interface.

    Two figures are always presented separately: bytes that can be reclaimed
    automatically, and bytes surfaced for review. Merging them would let a user
    believe space had been freed when it had only been listed.

    Color degrades automatically when stdout is redirected or NO_COLOR is set.
*)
module Report.Console

open System
open System.Text

open Types
open Utilities

/// True when ANSI color is appropriate for this stdout.
let private useColor () =
    not Console.IsOutputRedirected
    && String.IsNullOrEmpty(Environment.GetEnvironmentVariable "NO_COLOR")

let private reset = "\027[0m"
let private bold = "\027[1m"
let private dim = "\027[90m"

/// Wraps text in a color when color is enabled.
let private colorize (color: string) (text: string) =
    if useColor () then color + text + reset else text

/// Renders the risk icon and label for a finding.
let private riskBadge (level: RiskLevel) =
    let d = RiskLevel.describe level
    colorize d.AnsiColor (sprintf "%s %-12s" d.Icon (RiskLevel.toText level))

/// Renders a horizontal rule.
let private rule () = String.replicate 72 "─"

/// Renders the complete console report.
///
/// `showLargeFiles` corresponds to --large-files and adds a dedicated top-50
/// section beyond the always-present top-10 "Largest items" summary.
let render
    (snapshots: Snapshots.LocalSnapshot list)
    (result: Scanner.ScanResult)
    (showLargeFiles: bool)
    : string =
    let sb = StringBuilder()
    let append (text: string) = sb.AppendLine text |> ignore

    let reclaimable = Scanner.reclaimableBytes result
    let pending = Scanner.pendingReviewBytes result
    let wasted = Scanner.wastedBytes result

    append ""
    append (colorize bold "DiskCleanupAdvisor")
    append (rule ())
    append (sprintf "Platform     %A" result.Platform)
    append (sprintf "Scanned      %d roots, %d items" result.ScanRoots.Length result.Findings.Length)
    append (sprintf "Duration     %s" (Format.duration result.Elapsed))
    append ""

    // The two figures, always distinct.
    append (colorize bold "Space")
    append (sprintf "  Reclaimable      %s   %s"
                (colorize "\027[32m" (Format.bytes reclaimable))
                (colorize dim "eligible for automatic removal"))
    append (sprintf "  Pending review   %s   %s"
                (colorize "\027[33m" (Format.bytes pending))
                (colorize dim "requires your judgment"))
    if not (List.isEmpty result.Duplicates) then
        append (sprintf "  Duplicate waste  %s   %s"
                    (colorize "\027[33m" (Format.bytes wasted))
                    (colorize dim "review only — never reclaimable automatically"))
    append ""

    // Per-category breakdown, largest first.
    let grouped = Scanner.byCategory result
    if List.isEmpty grouped then
        append (colorize dim "  Nothing found.")
    else
        append (colorize bold "By category")
        append (sprintf "  %-26s %10s %10s %8s" "CATEGORY" "TOTAL" "RECLAIM" "ITEMS")
        for category, findings in grouped do
            let total = findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
            let claimable =
                findings |> List.filter (fun f -> f.Candidate.IsSome) |> List.sumBy (fun f -> f.Meta.SizeBytes)
            append (sprintf "  %-26s %10s %10s %8d"
                        (Category.toText category)
                        (Format.bytes total)
                        (Format.bytes claimable)
                        findings.Length)
    append ""

    // The ten largest individual findings.
    let largest =
        result.Findings
        |> List.sortByDescending (fun f -> f.Meta.SizeBytes)
        |> List.truncate 10
    if not (List.isEmpty largest) then
        append (colorize bold "Largest items")
        for f in largest do
            append (sprintf "  %s %10s  %s" (riskBadge f.Risk) (Format.bytes f.Meta.SizeBytes) f.Meta.Path)
        append ""

    // A fuller top-50 section on request, beyond the always-present top-10
    // above. Kept as a distinct block rather than folded into "Largest
    // items" so --large-files has a visibly different, more complete report.
    if showLargeFiles then
        let top50 =
            result.Findings
            |> List.sortByDescending (fun f -> f.Meta.SizeBytes)
            |> List.truncate 50
        append (colorize bold "Large files (top 50)")
        for f in top50 do
            append (sprintf "  %s %10s  %s" (riskBadge f.Risk) (Format.bytes f.Meta.SizeBytes) f.Meta.Path)
        append ""

    // Duplicate files (--duplicates). Review only: identical bytes do not
    // make a copy disposable, so nothing here ever becomes a deletion
    // candidate or contributes to reclaimableBytes.
    if not (List.isEmpty result.Duplicates) then
        append (colorize bold "Duplicate files")
        append (sprintf "  %d groups, %s wasted across redundant copies (review only)"
                    result.Duplicates.Length (Format.bytes wasted))
        for g in result.Duplicates |> List.truncate 10 do
            append (sprintf "  %s wasted  %d copies  keeper: %s"
                        (Format.bytes g.Wasted) g.Paths.Length g.Keeper)
            for p in g.Paths |> List.filter (fun p -> p <> g.Keeper) |> List.truncate 4 do
                append (sprintf "      %s" p)
        append ""

    // Diagnostics.
    append (colorize bold "Diagnostics")
    append (Diagnostics.render result.Counters)
    append ""

    // The Full Disk Access hint — actionable, and the difference between a
    // complete scan and a silently partial one.
    let unreadable = Scanner.unreadableDirectoryCount result
    if unreadable > 0 then
        append (colorize "\027[33m" (sprintf "  %d directories were unreadable." unreadable))
        append (colorize dim "  Grant Full Disk Access to your terminal in System Settings →")
        append (colorize dim "  Privacy & Security to include them in the scan.")
        append ""

    // Time Machine snapshots — reported because no file deletion reclaims
    // them, and a user who frees caches but sees no free-space change has
    // been misled if this is left unmentioned.
    let snapshotAdvice = Snapshots.renderAdvice snapshots
    if snapshotAdvice <> "" then
        append (colorize bold "Time Machine")
        append snapshotAdvice
        append ""

    if reclaimable > 0L then
        append (colorize dim "  This was a dry run. Nothing was modified.")
        append (colorize dim "  Run with --delete to move the reclaimable items to Trash.")
        append ""

    sb.ToString()

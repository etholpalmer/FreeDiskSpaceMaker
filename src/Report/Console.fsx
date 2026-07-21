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
let render (result: Scanner.ScanResult) : string =
    let sb = StringBuilder()
    let append (text: string) = sb.AppendLine text |> ignore

    let reclaimable = Scanner.reclaimableBytes result
    let pending = Scanner.pendingReviewBytes result

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

    if reclaimable > 0L then
        append (colorize dim "  This was a dry run. Nothing was modified.")
        append (colorize dim "  Run with --delete to move the reclaimable items to Trash.")
        append ""

    sb.ToString()

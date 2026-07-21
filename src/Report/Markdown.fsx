(**
    Markdown report. Suitable for pasting into notes or committing to a repo.

    Renders from the same ScanResult as every other format; no format has
    access to data the others lack.
*)
module Report.Markdown

open System
open System.Text

/// Escapes the pipe character so a path cannot break out of a table cell.
let private cell (text: string) : string = text.Replace("|", "\\|")

/// Renders the full Markdown report.
let render (result: Scanner.ScanResult) : string =
    let sb = StringBuilder()
    let line (text: string) = sb.AppendLine text |> ignore

    line "# DiskCleanupAdvisor"
    line ""
    line (sprintf "Generated %s on %A." (result.GeneratedAtUtc.ToString "u") result.Platform)
    line ""
    line "## Space"
    line ""
    line (sprintf "- **Reclaimable:** %s — eligible for automatic removal"
              (Utilities.Format.bytes (Scanner.reclaimableBytes result)))
    line (sprintf "- **Pending review:** %s — requires your judgment"
              (Utilities.Format.bytes (Scanner.pendingReviewBytes result)))
    line ""
    line "Removal moves items to the Trash. Space is reclaimed when you empty it."
    line ""
    line "## By category"
    line ""
    line "| Category | Total | Reclaimable | Items |"
    line "|---|---:|---:|---:|"
    for category, findings in Scanner.byCategory result do
        let total = findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
        let claimable =
            findings |> List.filter (fun f -> f.Candidate.IsSome) |> List.sumBy (fun f -> f.Meta.SizeBytes)
        line (sprintf "| %s | %s | %s | %d |"
                  (cell (Types.Category.toText category))
                  (Utilities.Format.bytes total)
                  (Utilities.Format.bytes claimable)
                  findings.Length)
    line ""
    line "## Largest items"
    line ""
    line "| Risk | Size | Path | Rule |"
    line "|---|---:|---|---|"
    for f in result.Findings |> List.sortByDescending (fun f -> f.Meta.SizeBytes) |> List.truncate 50 do
        line (sprintf "| %s | %s | `%s` | %s |"
                  (Types.RiskLevel.toText f.Risk)
                  (Utilities.Format.bytes f.Meta.SizeBytes)
                  (cell f.Meta.Path)
                  (cell f.RuleName))
    line ""

    let unreadable = Scanner.unreadableDirectoryCount result
    if unreadable > 0 then
        line (sprintf "> %d directories were unreadable. Grant Full Disk Access to your terminal to include them." unreadable)
        line ""

    sb.ToString()

/// Writes the Markdown report to disk.
let write (path: string) (result: Scanner.ScanResult) : Result<unit, string> =
    try
        let directory = IO.Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (IO.Directory.Exists directory) then
            IO.Directory.CreateDirectory directory |> ignore
        IO.File.WriteAllText(path, render result)
        Ok ()
    with ex -> Error (sprintf "cannot write %s: %s" path ex.Message)

(**
    JSON report.

    Projects the domain types through the DTO layer before serializing, because
    System.Text.Json cannot handle F# discriminated unions. This module is also
    the fixture format for tests and enables diffing two scans.
*)
module Report.Json

open System
open System.Text.Json

open Types

/// Projects one finding into its serialization-safe form.
let private findingToDto (now: DateTime) (f: Scanner.Finding) : Dto.FindingDto =
    { Path = f.Meta.Path
      RuleName = f.RuleName
      Category = Category.toText f.Category
      Risk = RiskLevel.toText f.Risk
      Recoverability = Recoverability.toText f.Recoverability
      Strategy = DeleteStrategy.toText f.Strategy
      SizeBytes = f.Meta.SizeBytes
      AgeDays = (FileMeta.age now f.Meta).TotalDays
      SafeToDelete = f.Candidate.IsSome
      Reason = f.Reason }

/// Projects one duplicate group into its serialization-safe form.
let private duplicateToDto (g: Duplicates.DuplicateGroup) : Dto.DuplicateGroupDto =
    { SizeBytes = g.SizeBytes
      Paths = g.Paths
      Keeper = g.Keeper
      Wasted = g.Wasted }

/// Projects a whole scan result into its serialization-safe form.
let toDto (result: Scanner.ScanResult) : Dto.ScanResultDto =
    let now = result.GeneratedAtUtc
    let categories =
        Scanner.byCategory result
        |> List.map (fun (category, findings) ->
            let summary : Dto.CategorySummaryDto =
                { Category = Category.toText category
                  ItemCount = findings.Length
                  TotalBytes = findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
                  ReclaimableBytes =
                    findings
                    |> List.filter (fun f -> f.Candidate.IsSome)
                    |> List.sumBy (fun f -> f.Meta.SizeBytes) }
            summary)

    { GeneratedAtUtc = result.GeneratedAtUtc.ToString "o"
      Platform = sprintf "%A" result.Platform
      ScanRoots = result.ScanRoots
      TotalItems = result.Findings.Length
      TotalBytes = result.Findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
      ReclaimableBytes = Scanner.reclaimableBytes result
      PendingReviewBytes = Scanner.pendingReviewBytes result
      DuplicateWastedBytes = Scanner.wastedBytes result
      Categories = categories
      Findings = result.Findings |> List.map (findingToDto now)
      Duplicates = result.Duplicates |> List.map duplicateToDto
      Errors = result.Errors |> List.map FsError.toText
      UnreadableDirectoryCount = Scanner.unreadableDirectoryCount result
      ElapsedSeconds = result.Elapsed.TotalSeconds }

/// Writes a JSON report to disk.
let write (path: string) (result: Scanner.ScanResult) : Result<unit, string> =
    try
        let json = JsonSerializer.Serialize(toDto result, Dto.serializerOptions)
        let directory = IO.Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (IO.Directory.Exists directory) then
            IO.Directory.CreateDirectory directory |> ignore
        IO.File.WriteAllText(path, json)
        Ok ()
    with ex -> Error (sprintf "cannot write %s: %s" path ex.Message)

(**
    Tests for the Report.Dto serialization boundary.

    The critical guard here is the first test: System.Text.Json throws
    NotSupportedException on F# discriminated unions, so this test proves
    no DU has crept into a DTO field.
*)
open System.Text.Json
open TestFramework
open Dto

let run () =
    suite "Report.Dto"

    let sampleFinding =
        { Path = "/testroot/a.log"
          RuleName = "User Logs"
          Category = "Logs"
          Risk = "Safe"
          Recoverability = "Regenerated"
          Strategy = "ContentsOnly"
          SizeBytes = 4096L
          AgeDays = 12.5
          SafeToDelete = true
          Reason = "Diagnostic output." }

    let sampleResult =
        { GeneratedAtUtc = "2026-07-20T12:00:00Z"
          Platform = "MacOS"
          ScanRoots = [ "/testroot" ]
          TotalItems = 1
          TotalBytes = 4096L
          ReclaimableBytes = 4096L
          PendingReviewBytes = 0L
          DuplicateWastedBytes = 0L
          Categories = [ { Category = "Logs"; ItemCount = 1; TotalBytes = 4096L; ReclaimableBytes = 4096L } ]
          FindingsIncluded = 1
          FindingsTruncated = false
          Findings = [ sampleFinding ]
          Duplicates = []
          Errors = []
          UnreadableDirectoryCount = 0
          ElapsedSeconds = 1.25 }

    test "a finding serializes without throwing" (fun () ->
        // System.Text.Json throws NotSupportedException on F# discriminated
        // unions. This test is the guard that keeps DUs out of the DTOs.
        let json = JsonSerializer.Serialize(sampleFinding, serializerOptions)
        assertContains "\"path\"" json "camelCase property naming"
        assertContains "User Logs" json "rule name present")

    test "a full scan result round-trips" (fun () ->
        let json = JsonSerializer.Serialize(sampleResult, serializerOptions)
        let restored = JsonSerializer.Deserialize<ScanResultDto>(json, serializerOptions)
        assertEqual sampleResult.TotalBytes restored.TotalBytes "total bytes survive"
        assertEqual 1 restored.Findings.Length "finding survives"
        assertEqual "User Logs" (List.head restored.Findings).RuleName "rule name survives")

    test "an empty scan result serializes" (fun () ->
        let empty = { sampleResult with Findings = []; Categories = []; TotalItems = 0 }
        let json = JsonSerializer.Serialize(empty, serializerOptions)
        assertContains "\"findings\":[]" (json.Replace(" ", "")) "empty array, not null")

    test "a duplicate group serializes without a risk field, without throwing" (fun () ->
        // DuplicateGroupDto has no DU fields either — this is the same guard
        // as the finding test, applied to the duplicates DTO.
        let group : DuplicateGroupDto =
            { SizeBytes = 4096L; Paths = [ "/a"; "/b" ]; Keeper = "/a"; Wasted = 4096L }
        let withDuplicate = { sampleResult with Duplicates = [ group ]; DuplicateWastedBytes = 4096L }
        let json = JsonSerializer.Serialize(withDuplicate, serializerOptions)
        assertContains "\"duplicates\"" json "duplicates present"
        assertContains "\"duplicateWastedBytes\":4096" (json.Replace(" ", "").Replace("\n", "")) "wasted bytes present")

    test "output is indented for human readability" (fun () ->
        let json = JsonSerializer.Serialize(sampleResult, serializerOptions)
        assertContains "\n" json "indented output contains newlines")

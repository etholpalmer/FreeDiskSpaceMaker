(**
    Serialization-safe projections of the domain types.

    System.Text.Json throws NotSupportedException on F# discriminated unions.
    Rather than register custom converters for each one, every DU is projected
    to a primitive string here, at a single boundary. The rule for this module
    is absolute: **no field of any DTO may be a discriminated union.**

    These records are also the JSON report's public contract, so their field
    names are part of the tool's interface and should change only deliberately.
*)
module Dto

open System
open System.Text.Json
open System.Text.Json.Serialization

/// One reported item.
type FindingDto =
    { Path: string
      RuleName: string
      Category: string
      Risk: string
      Recoverability: string
      Strategy: string
      SizeBytes: int64
      AgeDays: float
      SafeToDelete: bool
      Reason: string }

/// One group of byte-identical files (--duplicates). Review only: a
/// duplicate group never corresponds to a deletion candidate, so this DTO
/// deliberately has no risk or safe-to-delete field.
type DuplicateGroupDto =
    { SizeBytes: int64
      Paths: string list
      Keeper: string
      Wasted: int64 }

/// Aggregate figures for one category.
type CategorySummaryDto =
    { Category: string
      ItemCount: int
      TotalBytes: int64
      /// Bytes that passed all three gates and could be moved to Trash.
      ReclaimableBytes: int64 }

/// A complete scan, as written to the JSON report.
type ScanResultDto =
    { GeneratedAtUtc: string
      Platform: string
      ScanRoots: string list
      TotalItems: int
      TotalBytes: int64
      /// Bytes eligible for automatic removal.
      ReclaimableBytes: int64
      /// Bytes surfaced for human judgment. Deliberately kept distinct from
      /// ReclaimableBytes: conflating them would let a user believe space had
      /// been freed when it had only been listed.
      PendingReviewBytes: int64
      /// Bytes consumed by redundant duplicate copies. Deliberately its own
      /// figure — never summed with ReclaimableBytes or PendingReviewBytes,
      /// since duplicate groups never contribute a deletion candidate.
      DuplicateWastedBytes: int64
      Categories: CategorySummaryDto list
      /// How many findings this document actually carries. Equals TotalItems
      /// only when the report is complete.
      FindingsIncluded: int
      /// True when Findings holds a subset. A truncated report that looked
      /// complete would be the same class of defect as a silently partial
      /// scan, so consumers are told outright rather than left to compare
      /// counts.
      FindingsTruncated: bool
      Findings: FindingDto list
      Duplicates: DuplicateGroupDto list
      Errors: string list
      UnreadableDirectoryCount: int
      ElapsedSeconds: float }

/// Shared serializer settings: camelCase names, indented output, and no
/// converters for DUs — because no DTO here contains one.
let serializerOptions =
    let options =
        JsonSerializerOptions(
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never)
    options

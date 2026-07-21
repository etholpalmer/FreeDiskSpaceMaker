(**
    JSON configuration loading, validation, and merging over defaults.

    Validation accumulates every error rather than stopping at the first, so a
    user with three mistakes fixes them in one pass instead of three runs.

    Parsing is done against JsonDocument rather than deserializing into an F#
    record directly: System.Text.Json cannot handle F# discriminated unions and
    silently accepts unknown fields, both of which we want to control here.
*)
module Configuration

open System
open System.Text.Json

open Types
open Utilities

/// User-supplied settings, merged over `defaults`.
type Config =
    { /// Absolute paths excluded from scanning entirely.
      IgnorePaths: string list
      /// Lowercase extensions (with leading dot) excluded from results.
      IgnoreExtensions: string list
      /// Files below this size are ignored for hashing (--duplicates) and are
      /// the floor `Duplicates.find` is called with. This is deliberately
      /// separate from `FindingsMinSizeBytes` below: 1 MB is a sensible
      /// default for "don't bother hashing tiny files" but would be a
      /// destructive default for "don't report tiny files" — it would
      /// silently drop most findings from every report.
      MinSizeBytes: int64
      /// Additional size floor applied to every finding (--min-size).
      /// Zero by default, so a plain scan is unaffected: only an explicit
      /// --min-size (or config value) narrows the report.
      FindingsMinSizeBytes: int64
      /// Additional age floor applied to every finding, on top of each
      /// rule's own MinAge (--age). Zero means no additional floor, so a
      /// plain scan is unaffected.
      AgeThresholdDays: int
      /// Traversal parallelism.
      ThreadCount: int
      /// "sha256" or "md5".
      HashAlgorithm: string
      /// Rule name to a replacement risk level. May only raise severity;
      /// the direction is enforced by RiskEngine, which knows declared risks.
      RiskOverrides: Map<string, RiskLevel> }

/// Settings used when no configuration file is present.
let defaults: Config =
    { IgnorePaths = []
      IgnoreExtensions = []
      MinSizeBytes = 1_048_576L
      FindingsMinSizeBytes = 0L
      AgeThresholdDays = 0
      ThreadCount = min Environment.ProcessorCount 8
      HashAlgorithm = "sha256"
      RiskOverrides = Map.empty }

/// The conventional configuration location when `--config` is not supplied.
let defaultPath () : string =
    IO.Path.Combine(
        Environment.GetFolderPath Environment.SpecialFolder.UserProfile,
        ".config", "diskcleanupadvisor", "config.json")

/// Reads an optional property, returning None when absent or null.
let private tryProperty (root: JsonElement) (name: string) : JsonElement option =
    match root.TryGetProperty name with
    | true, value when value.ValueKind <> JsonValueKind.Null -> Some value
    | _ -> None

/// Reads an optional string array.
let private readStringArray (root: JsonElement) (name: string) : Result<string list option, string> =
    match tryProperty root name with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Array ->
        value.EnumerateArray()
        |> Seq.map (fun e -> e.GetString())
        |> Seq.filter (isNull >> not)
        |> Seq.toList
        |> Some
        |> Ok
    | Some _ -> Error (sprintf "%s must be an array of strings" name)

/// Reads an optional integer.
let private readInt (root: JsonElement) (name: string) : Result<int option, string> =
    match tryProperty root name with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Number ->
        match value.TryGetInt32() with
        | true, n -> Ok (Some n)
        | _ -> Error (sprintf "%s must be a 32-bit integer" name)
    | Some _ -> Error (sprintf "%s must be a number" name)

/// Reads an optional 64-bit integer.
let private readInt64 (root: JsonElement) (name: string) : Result<int64 option, string> =
    match tryProperty root name with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Number ->
        match value.TryGetInt64() with
        | true, n -> Ok (Some n)
        | _ -> Error (sprintf "%s must be a 64-bit integer" name)
    | Some _ -> Error (sprintf "%s must be a number" name)

/// Reads an optional string.
let private readString (root: JsonElement) (name: string) : Result<string option, string> =
    match tryProperty root name with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.String -> Ok (Some (value.GetString()))
    | Some _ -> Error (sprintf "%s must be a string" name)

/// Reads the riskOverrides object, parsing each level name.
let private readOverrides (root: JsonElement) : Result<Map<string, RiskLevel> option, string list> =
    match tryProperty root "riskOverrides" with
    | None -> Ok None
    | Some value when value.ValueKind = JsonValueKind.Object ->
        let parsed =
            value.EnumerateObject()
            |> Seq.map (fun prop ->
                if prop.Value.ValueKind <> JsonValueKind.String then
                    Error (sprintf "riskOverrides.%s must be a string" prop.Name)
                else
                    let raw = prop.Value.GetString()
                    match RiskLevel.parse raw with
                    | Ok level -> Ok (prop.Name, level)
                    | Error _ -> Error (sprintf "riskOverrides.%s: unknown risk level '%s'" prop.Name raw))
            |> Seq.toList
        let errors = parsed |> List.choose (function Error e -> Some e | Ok _ -> None)
        if List.isEmpty errors then
            Ok (Some (parsed |> List.choose (function Ok kv -> Some kv | Error _ -> None) |> Map.ofList))
        else Error errors
    | Some _ -> Error [ "riskOverrides must be an object" ]

/// Validates a fully-merged configuration, returning every problem found.
let private validate (config: Config) : Result<Config, string list> =
    let errors =
        [ if config.MinSizeBytes < 0L then
            yield sprintf "minSizeBytes must be zero or greater (got %d)" config.MinSizeBytes
          if config.FindingsMinSizeBytes < 0L then
            yield sprintf "findingsMinSizeBytes must be zero or greater (got %d)" config.FindingsMinSizeBytes
          if config.AgeThresholdDays < 0 then
            yield sprintf "ageThresholdDays must be zero or greater (got %d)" config.AgeThresholdDays
          if config.ThreadCount < 1 then
            yield sprintf "threadCount must be at least 1 (got %d)" config.ThreadCount
          elif config.ThreadCount > 64 then
            yield sprintf "threadCount must be 64 or fewer (got %d)" config.ThreadCount
          if config.HashAlgorithm <> "sha256" && config.HashAlgorithm <> "md5" then
            yield sprintf "hashAlgorithm must be 'sha256' or 'md5' (got '%s')" config.HashAlgorithm ]
    if List.isEmpty errors then Ok config else Error errors

/// Parses configuration JSON, merging supplied values over the defaults.
/// Returns every validation error at once.
let parse (json: string) : Result<Config, string list> =
    let documentResult =
        try Ok (JsonDocument.Parse json)
        with :? JsonException as ex -> Error [ sprintf "invalid JSON: %s" ex.Message ]

    match documentResult with
    | Error errors -> Error errors
    | Ok document ->
        use document = document
        let root = document.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            Error [ "configuration root must be a JSON object" ]
        else
            // Collect field-level read errors before validating semantics.
            let ignorePaths = readStringArray root "ignorePaths"
            let ignoreExtensions = readStringArray root "ignoreExtensions"
            let minSize = readInt64 root "minSizeBytes"
            let findingsMinSize = readInt64 root "findingsMinSizeBytes"
            let ageThreshold = readInt root "ageThresholdDays"
            let threadCount = readInt root "threadCount"
            let hashAlgorithm = readString root "hashAlgorithm"
            let overrides = readOverrides root

            let readErrors =
                [ match ignorePaths with Error e -> yield e | _ -> ()
                  match ignoreExtensions with Error e -> yield e | _ -> ()
                  match minSize with Error e -> yield e | _ -> ()
                  match findingsMinSize with Error e -> yield e | _ -> ()
                  match ageThreshold with Error e -> yield e | _ -> ()
                  match threadCount with Error e -> yield e | _ -> ()
                  match hashAlgorithm with Error e -> yield e | _ -> ()
                  match overrides with Error es -> yield! es | _ -> () ]

            if not (List.isEmpty readErrors) then Error readErrors
            else
                let pick result fallback =
                    match result with
                    | Ok (Some v) -> v
                    | _ -> fallback

                let merged =
                    { IgnorePaths =
                        pick ignorePaths defaults.IgnorePaths
                        |> List.map (Utilities.Path.expandHome >> Utilities.Path.normalize)
                      IgnoreExtensions =
                        pick ignoreExtensions defaults.IgnoreExtensions
                        |> List.map (fun e ->
                            let lower = e.Trim().ToLowerInvariant()
                            if lower.StartsWith "." then lower else "." + lower)
                      MinSizeBytes = pick minSize defaults.MinSizeBytes
                      FindingsMinSizeBytes = pick findingsMinSize defaults.FindingsMinSizeBytes
                      AgeThresholdDays = pick ageThreshold defaults.AgeThresholdDays
                      ThreadCount = pick threadCount defaults.ThreadCount
                      HashAlgorithm = (pick hashAlgorithm defaults.HashAlgorithm).Trim().ToLowerInvariant()
                      RiskOverrides = pick overrides defaults.RiskOverrides }

                validate merged

/// Loads configuration from an explicit path, the conventional path, or
/// falls back to defaults when neither exists.
///
/// An explicitly requested path that is missing is an error; a missing
/// conventional path is not.
let load (fs: Platform.FileSystemOps) (explicitPath: string option) : Result<Config, string list> =
    let readFile path =
        try Ok (IO.File.ReadAllText path)
        with ex -> Error [ sprintf "cannot read %s: %s" path ex.Message ]

    match explicitPath with
    | Some path ->
        if not (fs.Exists path) then Error [ sprintf "configuration file not found: %s" path ]
        else readFile path |> Result.bind parse
    | None ->
        let conventional = defaultPath ()
        if fs.Exists conventional then readFile conventional |> Result.bind parse
        else Ok defaults

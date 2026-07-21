(**
    Assembles a scan: walk each rule root, match files against rules in
    precedence order, and ask the risk engine whether each match may be removed.

    The scanner contains **no rule-specific logic**. It is parameterized over a
    resolved rule list, so adding a rule to the catalog changes nothing here.

    First match wins. Because rules arrive sorted most-specific-first, a narrow
    rule inside a broad one takes precedence — which is exactly what makes the
    ~/Library/Containers requirement work: the cache rule is checked before the
    catch-all, so only Data/Library/Caches and Data/tmp come back Safe.
*)
module Scanner

open System

open Types
open Utilities

/// One reported item, with its deletion candidate when it passed all gates.
type Finding =
    { Meta: FileMeta
      RuleName: string
      Category: Category
      Risk: RiskLevel
      Recoverability: Recoverability
      Strategy: DeleteStrategy
      Reason: string
      /// Present only when all three risk gates passed. `None` means the item
      /// is reported but may not be removed.
      Candidate: RiskEngine.DeletionCandidate option }

/// A complete scan.
type ScanResult =
    { GeneratedAtUtc: DateTime
      Platform: Types.Platform
      ScanRoots: string list
      Findings: Finding list
      Errors: FsError list
      Elapsed: TimeSpan
      Counters: Diagnostics.Snapshot }

/// True when a file satisfies a rule's include globs, exclude globs, age
/// floor, and size floor.
let private satisfies (now: DateTime) (root: string) (rule: Rules.Rule) (meta: FileMeta) : bool =
    // Globs are matched against the path relative to the rule root, so a rule
    // does not have to know where it was resolved to.
    let relative =
        let normalizedRoot = Utilities.Path.normalize root
        let normalizedPath = Utilities.Path.normalize meta.Path
        if normalizedPath.Length > normalizedRoot.Length + 1
        then normalizedPath.Substring(normalizedRoot.Length + 1)
        else normalizedPath

    let includeOk =
        List.isEmpty rule.Include || Utilities.Glob.matchesAny rule.Include relative

    let excludeOk =
        List.isEmpty rule.Exclude || not (Utilities.Glob.matchesAny rule.Exclude relative)

    let ageOk =
        match rule.MinAge with
        | None -> true
        | Some minimum -> FileMeta.age now meta >= minimum

    let sizeOk =
        match rule.MinSize with
        | None -> true
        | Some minimum -> meta.SizeBytes >= minimum

    includeOk && excludeOk && ageOk && sizeOk

/// Finds the first rule in precedence order that claims this file.
let private firstMatch
    (now: DateTime)
    (rules: Rules.ResolvedRule list)
    (meta: FileMeta)
    : Rules.ResolvedRule option =
    rules
    |> List.tryFind (fun resolved ->
        Utilities.Path.isDescendantOf resolved.Root meta.Path
        && satisfies now resolved.Root resolved.Rule meta)

/// Runs a scan over every resolved rule root.
///
/// Roots that are nested inside another root are walked only once, from the
/// outermost ancestor — walking a broad root already visits everything
/// beneath a narrower one, so keeping both would report the same files
/// twice. `ScanResult.ScanRoots` reflects the roots actually walked after
/// this filtering.
///
/// `allowProbablySafe` corresponds to --force and widens only the risk gate.
/// Returns Error when configuration overrides are invalid, because continuing
/// would apply a risk policy the user did not actually get.
let scan
    (fs: Platform.FileSystemOps)
    (config: Configuration.Config)
    (allowProbablySafe: bool)
    (rules: Rules.ResolvedRule list)
    : Result<ScanResult, string list> =

    // Apply configuration overrides first. A lowering override is fatal.
    let overrideResults =
        rules
        |> List.map (fun resolved ->
            RiskEngine.applyOverrides config.RiskOverrides resolved.Rule
            |> Result.map (fun updated -> { resolved with Rules.Rule = updated }))

    let overrideErrors =
        overrideResults |> List.choose (function Error e -> Some e | Ok _ -> None)

    if not (List.isEmpty overrideErrors) then Error overrideErrors
    else

    let effectiveRules =
        overrideResults |> List.choose (function Ok r -> Some r | Error _ -> None)

    let now = DateTime.UtcNow
    let counters = Diagnostics.create ()
    let walkOptions =
        { DirectoryWalker.defaultOptions with
            DirectoryWalker.MaxDepth = 32
            DirectoryWalker.ThreadCount = config.ThreadCount
            DirectoryWalker.IgnorePaths = config.IgnorePaths }

    // Distinct roots, so two rules sharing a root do not walk it twice.
    let distinctRoots =
        effectiveRules
        |> List.map (fun r -> r.Root)
        |> List.distinct

    // Drop any root that is a descendant of another root in the list.
    // Walking the outermost root already visits every file beneath a nested
    // root, so keeping the nested root too would walk (and report) those
    // files twice. Rule matching is unaffected: `firstMatch` still tests
    // every rule in precedence order against every file, regardless of
    // which root the walk found it under.
    let roots =
        distinctRoots
        |> List.filter (fun candidate ->
            distinctRoots
            |> List.forall (fun other ->
                other = candidate || not (Utilities.Path.isDescendantOf other candidate)))

    let outcomes = roots |> List.map (DirectoryWalker.walk fs counters walkOptions)

    let allFiles = outcomes |> List.collect (fun o -> o.Files)
    let allErrors = outcomes |> List.collect (fun o -> o.Errors)

    let ignoredExtension (path: string) =
        let lower = path.ToLowerInvariant()
        config.IgnoreExtensions |> List.exists (fun e -> lower.EndsWith(e, StringComparison.Ordinal))

    let findings =
        allFiles
        |> List.filter (fun meta -> not (ignoredExtension meta.Path))
        |> List.choose (fun meta ->
            match firstMatch now effectiveRules meta with
            | None -> None
            | Some resolved ->
                let rule = resolved.Rule
                // The risk engine is the sole authority on deletability. A
                // rejection here is normal, not an error: most findings are
                // reported without being removable.
                let candidate =
                    match RiskEngine.evaluate allowProbablySafe resolved meta with
                    | Ok c -> Some c
                    | Error _ -> None
                Some
                    { Meta = meta
                      RuleName = rule.Name
                      Category = rule.Category
                      Risk = rule.Risk
                      Recoverability = rule.Recoverability
                      Strategy = rule.Strategy
                      Reason = rule.Reason
                      Candidate = candidate })
        // Defensive second layer: root de-duplication should already make
        // this a no-op, but if the same path were ever produced twice, keep
        // only the first occurrence (the highest-precedence match, since
        // rules are precedence-sorted) and preserve relative order.
        |> List.distinctBy (fun f -> Utilities.Path.normalize f.Meta.Path)

    Ok
        { GeneratedAtUtc = now
          Platform = Platform.current ()
          ScanRoots = roots
          Findings = findings
          Errors = allErrors
          Elapsed = counters.Clock.Elapsed
          Counters = Diagnostics.snapshot counters }

/// Total bytes that passed every gate and could be moved to Trash.
let reclaimableBytes (result: ScanResult) : int64 =
    result.Findings
    |> List.filter (fun f -> f.Candidate.IsSome)
    |> List.sumBy (fun f -> f.Meta.SizeBytes)

/// Total bytes reported for human judgment but not automatically removable.
/// Deliberately distinct from `reclaimableBytes` — see the report modules,
/// which must never present the two as one number.
let pendingReviewBytes (result: ScanResult) : int64 =
    result.Findings
    |> List.filter (fun f -> f.Candidate.IsNone)
    |> List.sumBy (fun f -> f.Meta.SizeBytes)

/// Groups findings by category, largest total first, dropping empty categories.
let byCategory (result: ScanResult) : (Category * Finding list) list =
    result.Findings
    |> List.groupBy (fun f -> f.Category)
    |> List.sortByDescending (fun (_, findings) -> findings |> List.sumBy (fun f -> f.Meta.SizeBytes))

/// The count of directories that could not be read, for the Full Disk Access hint.
let unreadableDirectoryCount (result: ScanResult) : int =
    result.Errors
    |> List.filter (function PermissionDenied _ -> true | _ -> false)
    |> List.length

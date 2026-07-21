(**
    The safety core. Three independent gates decide whether anything may be
    removed. Each gate can only reject; none can approve on its own, and all
    three must pass.

      Gate 1  Rule assertion    — a matched rule must be Safe and SafeToDelete
      Gate 2  Allowlist anchor  — the path must be a strict descendant of the
                                  rule's resolved root, after normalization
      Gate 3  Denylist veto     — a hardcoded protected set, checked last

    The veto outranks --force and outranks configuration overrides. It has no
    bypass flag. Configuration overrides may only raise severity; an override
    that lowers it is a validation error rather than a silent clamp, because
    silently ignoring it would leave the user believing a restriction had been
    loosened when it had not.

    `DeletionCandidate` has a private constructor. The only way to obtain one
    is `evaluate`, so no caller anywhere in the codebase can hand the Cleaner
    an arbitrary path. "Delete /" is unreachable by construction rather than
    defended against at runtime.
*)
module RiskEngine

open System

open Types
open Utilities
open Rules

/// Why a path was refused. Every rejection is explicit and reportable.
type Rejection =
    /// The matched rule does not assert deletability.
    | NotSafeToDelete
    /// The rule's risk level exceeds what the current flags permit.
    | RiskTooHigh of RiskLevel
    /// The path is not a strict descendant of the rule's resolved root.
    | OutsideRuleRoot
    /// The path is inside a protected location. Names the protected ancestor.
    | Vetoed of string
    /// The path is a symlink; relocating it would move the link, not the target.
    | IsSymlink

module Rejection =
    /// A human-readable explanation for reports.
    let toText (r: Rejection) : string =
        match r with
        | NotSafeToDelete -> "the matching rule does not permit deletion"
        | RiskTooHigh level -> sprintf "risk level %s exceeds what the current flags allow" (RiskLevel.toText level)
        | OutsideRuleRoot -> "the path is not inside the rule's root directory"
        | Vetoed ancestor -> sprintf "the path is inside the protected location %s" ancestor
        | IsSymlink -> "the path is a symbolic link"

/// Paths that are never deletable, regardless of rules, flags, or config.
///
/// Returned as a function rather than a value because the home-relative
/// entries must resolve against the invoking user at call time.
let denylist () : string list =
    let home = Platform.home ()
    let underHome relative = IO.Path.Combine(home, relative)
    [ underHome "Documents"
      underHome "Desktop"
      underHome "Pictures"
      underHome "Movies"
      underHome "Music"
      underHome "Downloads"
      underHome "Library/Application Support"
      "/System"
      "/Library"
      "/Applications"
      "/usr"
      "/bin"
      "/sbin"
      "/etc"
      "/var/db" ]
    |> List.map Utilities.Path.normalize

/// Cache-safe subpaths that are exempt from the Application Support veto.
/// These are the only locations inside a protected directory that a rule may
/// legitimately target.
let private vetoExemptions () : string list =
    let home = Platform.home ()
    [ IO.Path.Combine(home, "Library/Application Support/CrashReporter") ]
    |> List.map Utilities.Path.normalize

/// True when a path is protected and must never be removed.
///
/// A path is vetoed when it is, or is inside, any denylist entry — unless it
/// is inside a narrower exemption. Additionally, any path inside a `.git`
/// directory is vetoed unconditionally: removing objects there can destroy
/// commits that exist nowhere else.
let isVetoed (path: string) : bool =
    let normalized = Utilities.Path.normalize path

    let inGitDirectory =
        Utilities.Path.segments normalized
        |> List.exists (fun segment -> segment = ".git")

    if inGitDirectory then true
    else
        let exempt =
            vetoExemptions ()
            |> List.exists (fun e ->
                Utilities.Path.equals e normalized || Utilities.Path.isDescendantOf e normalized)
        if exempt then false
        else
            denylist ()
            |> List.exists (fun protectedPath ->
                Utilities.Path.equals protectedPath normalized
                || Utilities.Path.isDescendantOf protectedPath normalized)

/// The protected ancestor responsible for a veto, for error reporting.
let private vetoingAncestor (path: string) : string =
    let normalized = Utilities.Path.normalize path
    if Utilities.Path.segments normalized |> List.exists (fun s -> s = ".git") then ".git"
    else
        denylist ()
        |> List.tryFind (fun p ->
            Utilities.Path.equals p normalized || Utilities.Path.isDescendantOf p normalized)
        |> Option.defaultValue "(protected)"

/// Applies a configuration risk override to a rule.
///
/// An override may only raise severity. Lowering is rejected with an
/// explanatory error rather than silently ignored. Raising risk also clears
/// `SafeToDelete`, so the two fields cannot drift out of agreement.
let applyOverrides (overrides: Map<string, RiskLevel>) (rule: Rule) : Result<Rule, string> =
    match overrides.TryFind rule.Name with
    | None -> Ok rule
    | Some requested ->
        let currentSeverity = RiskLevel.severity rule.Risk
        let requestedSeverity = RiskLevel.severity requested
        if requestedSeverity < currentSeverity then
            Error (
                sprintf
                    "riskOverrides cannot lower risk: rule '%s' is declared %s and the override requests %s. Overrides may only raise severity."
                    rule.Name (RiskLevel.toText rule.Risk) (RiskLevel.toText requested))
        elif requestedSeverity = currentSeverity then Ok rule
        else Ok { rule with Risk = requested; SafeToDelete = false }

/// A path that has passed all three gates and may be moved to Trash.
///
/// The constructor is private to this module. `evaluate` is the only way to
/// produce one, which is what makes an arbitrary path structurally incapable
/// of reaching the Cleaner.
type DeletionCandidate =
    private
        { Path: string
          RuleName: string
          Strategy: DeleteStrategy
          SizeBytes: int64 }

/// The path this candidate refers to.
let candidatePath (c: DeletionCandidate) : string = c.Path

/// The size in bytes of this candidate.
let candidateSize (c: DeletionCandidate) : int64 = c.SizeBytes

/// The name of the rule that produced this candidate.
let candidateRule (c: DeletionCandidate) : string = c.RuleName

/// The removal strategy declared by the producing rule.
let candidateStrategy (c: DeletionCandidate) : DeleteStrategy = c.Strategy

/// Runs all three gates against a file under a resolved rule.
///
/// `allowProbablySafe` corresponds to the --force flag. It widens gate 1 by
/// exactly one level and has no effect on gates 2 or 3.
let evaluate
    (allowProbablySafe: bool)
    (resolved: ResolvedRule)
    (file: FileMeta)
    : Result<DeletionCandidate, Rejection> =

    let rule = resolved.Rule

    // A symlink is refused before any gate: relocating it would move the link
    // rather than its target, which is never what the user asked for.
    if file.LinkTarget.IsSome then Error IsSymlink

    // Gate 1 — the rule must assert deletability at a permitted risk level.
    elif not rule.SafeToDelete then Error NotSafeToDelete
    else
        let permitted =
            match rule.Risk with
            | Safe -> true
            | ProbablySafe -> allowProbablySafe
            | Review | Dangerous | NeverDelete -> false

        if not permitted then Error (RiskTooHigh rule.Risk)

        // Gate 2 — the path must resolve to a strict descendant of the rule
        // root. isDescendantOf normalizes both operands first, so a traversal
        // escape is resolved before comparison rather than string-matched.
        elif not (Utilities.Path.isDescendantOf resolved.Root file.Path) then Error OutsideRuleRoot

        // Gate 3 — the veto, checked last so it overrides everything above.
        elif isVetoed file.Path then Error (Vetoed(vetoingAncestor file.Path))

        else
            Ok { Path = Utilities.Path.normalize file.Path
                 RuleName = rule.Name
                 Strategy = rule.Strategy
                 SizeBytes = file.SizeBytes }

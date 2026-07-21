(**
    Adversarial gate tests for the RiskEngine safety core. These tests are the
    safety guarantee for the whole tool: every gate that can reject a path
    must actually reject it, and no combination of flags or overrides may
    bypass the denylist veto.
*)
open System
open TestFramework
open Types
open Rules
open RiskEngine

/// Builds a FileMeta for a path with sensible defaults.
let private meta path size =
    { Path = path
      SizeBytes = size
      LastAccess = DateTime.UtcNow.AddDays -10.0
      LastModified = DateTime.UtcNow.AddDays -10.0
      Created = DateTime.UtcNow.AddDays -100.0
      IsDirectory = false
      LinkTarget = None
      Owner = "testuser"
      Mode = "rw-r--r--" }

/// A Safe, deletable rule rooted at /testroot.
let private safeRule =
    { Rule =
        { Name = "Test Safe Rule"
          Platforms = Set.ofList [ MacOS; Linux; Windows ]
          Root = Absolute "/testroot"
          Include = []
          Exclude = []
          Risk = Safe
          Recoverability = Regenerated
          Strategy = ContentsOnly
          MinAge = None
          MinSize = None
          SafeToDelete = true
          Description = "test"
          Reason = "test"
          EstimatedBenefit = "test"
          Examples = [ "/testroot/x" ]
          Category = Caches }
      Root = "/testroot" }

let run () =
    suite "RiskEngine gate 1 — rule assertion"

    test "a Safe rule with SafeToDelete produces a candidate" (fun () ->
        let c = assertOk (evaluate false safeRule (meta "/testroot/file.tmp" 100L)) "safe rule accepts"
        assertEqual "/testroot/file.tmp" (candidatePath c) "path preserved"
        assertEqual 100L (candidateSize c) "size preserved")

    test "a rule without SafeToDelete is rejected" (fun () ->
        let rule = { safeRule with Rule = { safeRule.Rule with SafeToDelete = false } }
        assertEqual NotSafeToDelete (assertError (evaluate false rule (meta "/testroot/f" 1L)) "rejected") "NotSafeToDelete")

    test "a Review rule is rejected even when SafeToDelete is set" (fun () ->
        // Defence in depth: the catalog test forbids this combination, but the
        // engine must not trust that a malformed rule cannot reach it.
        let rule = { safeRule with Rule = { safeRule.Rule with Risk = Review } }
        assertEqual (RiskTooHigh Review) (assertError (evaluate false rule (meta "/testroot/f" 1L)) "rejected") "RiskTooHigh")

    test "ProbablySafe is rejected by default and accepted only with the flag" (fun () ->
        let rule = { safeRule with Rule = { safeRule.Rule with Risk = ProbablySafe } }
        assertEqual (RiskTooHigh ProbablySafe)
            (assertError (evaluate false rule (meta "/testroot/f" 1L)) "rejected without flag") "rejected by default"
        assertOk (evaluate true rule (meta "/testroot/f" 1L)) "accepted with allowProbablySafe" |> ignore)

    test "Dangerous and NeverDelete are rejected even with the force flag" (fun () ->
        for level in [ Dangerous; NeverDelete ] do
            let rule = { safeRule with Rule = { safeRule.Rule with Risk = level } }
            assertEqual (RiskTooHigh level)
                (assertError (evaluate true rule (meta "/testroot/f" 1L)) "rejected")
                (sprintf "%A is never deletable" level))

    suite "RiskEngine gate 2 — allowlist anchoring"

    test "a path outside the rule root is rejected" (fun () ->
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/elsewhere/file" 1L)) "outside root") "OutsideRuleRoot")

    test "the rule root itself is not a candidate" (fun () ->
        // Deleting the root would remove the very directory the rule describes.
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/testroot" 1L)) "root itself") "root is not its own descendant")

    test "a traversal escape from inside the root is rejected" (fun () ->
        // The attack: a path that textually starts with the root but resolves outside it.
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/testroot/../etc/passwd" 1L)) "escape rejected")
            "dot-dot escape rejected")

    test "a deeper traversal escape is rejected" (fun () ->
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/testroot/a/b/../../../etc/passwd" 1L)) "deep escape")
            "multi-level escape rejected")

    test "a sibling directory sharing a name prefix is rejected" (fun () ->
        // /testroot-backup must not be treated as inside /testroot.
        assertEqual OutsideRuleRoot
            (assertError (evaluate false safeRule (meta "/testroot-backup/file" 1L)) "prefix collision")
            "prefix collision rejected")

    test "dot-dot that stays inside the root is accepted" (fun () ->
        assertOk (evaluate false safeRule (meta "/testroot/a/../b" 1L)) "inner dot-dot resolves inside" |> ignore)

    suite "RiskEngine gate 3 — denylist veto"

    test "protected user directories are vetoed" (fun () ->
        let home = Platform.home ()
        for folder in [ "Documents"; "Desktop"; "Pictures"; "Movies"; "Music" ] do
            let path = IO.Path.Combine(home, folder, "file.txt")
            assertTrue (isVetoed path) (sprintf "%s is vetoed" folder))

    test "system directories are vetoed" (fun () ->
        assertTrue (isVetoed "/System/Library/anything") "/System vetoed"
        assertTrue (isVetoed "/Applications/Xcode.app") "/Applications vetoed")

    test "any path inside a git repository is vetoed" (fun () ->
        // Deleting from inside .git can destroy unpushed commits.
        assertTrue (isVetoed "/anywhere/project/.git/objects/ab/cdef") ".git contents vetoed"
        assertTrue (isVetoed "/anywhere/project/.git") ".git itself vetoed")

    test "the veto overrides a Safe rule that would otherwise accept" (fun () ->
        // The critical case: a rule root that overlaps protected territory.
        let home = Platform.home ()
        let rule = { safeRule with
                       Rule = { safeRule.Rule with Root = Absolute home }
                       Root = home }
        let protectedPath = IO.Path.Combine(home, "Documents", "thesis.txt")
        match evaluate false rule (meta protectedPath 1L) with
        | Error (Vetoed _) -> ()
        | Error other -> assertTrue false (sprintf "expected Vetoed, got %A" other)
        | Ok _ -> assertTrue false "VETO FAILED — a protected path produced a candidate")

    test "the veto is not bypassed by the force flag" (fun () ->
        let home = Platform.home ()
        let rule = { safeRule with
                       Rule = { safeRule.Rule with Root = Absolute home }
                       Root = home }
        let protectedPath = IO.Path.Combine(home, "Desktop", "notes.txt")
        match evaluate true rule (meta protectedPath 1L) with
        | Error (Vetoed _) -> ()
        | _ -> assertTrue false "VETO FAILED — force bypassed the denylist")

    test "an ordinary path under a rule root is not vetoed" (fun () ->
        assertFalse (isVetoed "/testroot/cache/entry") "ordinary cache path allowed")

    suite "RiskEngine — symlinks"

    test "a symlink is never a candidate" (fun () ->
        // Following a link could relocate the target rather than the link.
        let link = { meta "/testroot/link" 0L with LinkTarget = Some "/elsewhere/real" }
        assertEqual IsSymlink (assertError (evaluate false safeRule link) "symlink rejected") "IsSymlink")

    suite "RiskEngine — override clamping"

    test "an override that raises risk is applied" (fun () ->
        let overrides = Map.ofList [ "Test Safe Rule", Review ]
        let updated = assertOk (applyOverrides overrides safeRule.Rule) "raising override applied"
        assertEqual Review updated.Risk "risk raised"
        assertFalse updated.SafeToDelete "raising risk also clears SafeToDelete")

    test "an override that lowers risk is an error, not a silent clamp" (fun () ->
        // Silently ignoring it would leave the user believing they had
        // loosened a restriction that in fact still applies.
        let reviewRule = { safeRule.Rule with Name = "Review Rule"; Risk = Review; SafeToDelete = false }
        let overrides = Map.ofList [ "Review Rule", Safe ]
        let message = assertError (applyOverrides overrides reviewRule) "lowering rejected"
        assertContains "Review Rule" message "names the rule"
        assertContains "lower" (message.ToLowerInvariant()) "explains the direction")

    test "an override naming an unknown rule is ignored without error" (fun () ->
        let overrides = Map.ofList [ "No Such Rule", Review ]
        let updated = assertOk (applyOverrides overrides safeRule.Rule) "unrelated override ignored"
        assertEqual Safe updated.Risk "unchanged")

    test "an override to the same level is accepted as a no-op" (fun () ->
        let overrides = Map.ofList [ "Test Safe Rule", Safe ]
        let updated = assertOk (applyOverrides overrides safeRule.Rule) "same level accepted"
        assertEqual Safe updated.Risk "unchanged")

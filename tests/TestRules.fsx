(**
    Tests for the rule catalog: field invariants, platform filtering, and the
    precedence rule that lets the Containers cache rule outrank the
    Containers catch-all.
*)
open System
open TestFramework
open Types
open Rules

/// Runs all Rules test suites.
let run () =
    suite "Rules.catalog"

    test "every rule has a non-empty name, description, and reason" (fun () ->
        for rule in catalog do
            assertTrue (rule.Name.Length > 0) "name is non-empty"
            assertTrue (rule.Description.Length > 0) (sprintf "%s has a description" rule.Name)
            assertTrue (rule.Reason.Length > 0) (sprintf "%s has a reason" rule.Name)
            assertTrue (rule.Examples.Length > 0) (sprintf "%s has at least one example" rule.Name))

    test "rule names are unique" (fun () ->
        let names = catalog |> List.map (fun r -> r.Name)
        assertEqual names.Length (names |> List.distinct |> List.length) "no duplicate rule names")

    test "every rule declares at least one platform" (fun () ->
        for rule in catalog do
            assertFalse (Set.isEmpty rule.Platforms) (sprintf "%s declares a platform" rule.Name))

    test "only Safe rules may assert SafeToDelete" (fun () ->
        // A rule claiming deletability at any other risk level is a
        // contradiction the risk engine would have to resolve at runtime.
        for rule in catalog do
            if rule.SafeToDelete then
                assertEqual Safe rule.Risk (sprintf "%s asserts SafeToDelete so must be Safe" rule.Name))

    test "no rule targets a protected user data location" (fun () ->
        let forbidden = [ "Documents"; "Desktop"; "Pictures"; "Movies"; "Music" ]
        for rule in catalog do
            if rule.SafeToDelete then
                for segment in forbidden do
                    let root = match rule.Root with
                               | HomeRelative r -> r
                               | Absolute a -> a
                               | EnvRelative (_, r) -> r
                    assertFalse (root.Contains segment)
                        (sprintf "%s must not target %s" rule.Name segment))

    suite "Rules.forPlatform"

    test "macOS rules are returned for macOS" (fun () ->
        let macRules = forPlatform MacOS
        assertTrue (macRules.Length >= 20) "macOS catalog is populated"
        for rule in macRules do
            assertTrue (rule.Platforms.Contains MacOS) (sprintf "%s applies to macOS" rule.Name))

    test "Linux and Windows have seeded catalogs" (fun () ->
        assertTrue ((forPlatform Linux).Length >= 4) "Linux seeded"
        assertTrue ((forPlatform Windows).Length >= 4) "Windows seeded")

    suite "Rules.byPrecedence"

    test "more specific roots sort before less specific ones" (fun () ->
        let specific =
            { Rule = catalog |> List.head; Root = "/a/b/c/d/e" }
        let general =
            { Rule = catalog |> List.head; Root = "/a" }
        let ordered = byPrecedence [ general; specific ]
        assertEqual "/a/b/c/d/e" (List.head ordered).Root "longest root first")

    test "the Containers cache rule outranks the Containers catch-all" (fun () ->
        // This is the specific requirement from the spec: only Data/Library/Caches
        // and Data/tmp inside ~/Library/Containers may be Safe; everything else
        // must fall through to Review.
        let macRules = forPlatform MacOS
        let cacheRule = macRules |> List.tryFind (fun r -> r.Name = "Container Caches")
        let catchAll = macRules |> List.tryFind (fun r -> r.Name = "Container Data")
        assertTrue cacheRule.IsSome "Container Caches rule exists"
        assertTrue catchAll.IsSome "Container Data catch-all exists"
        assertEqual Safe cacheRule.Value.Risk "container caches are Safe"
        assertEqual Review catchAll.Value.Risk "other container data is Review"
        assertFalse catchAll.Value.SafeToDelete "catch-all is not deletable")

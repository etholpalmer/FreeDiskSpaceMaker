(**
    Tests for the Types module.

    Covers domain type definitions, discriminated unions, and parsing functions.
*)

open TestFramework
open Types

let run () =
    suite "Types.RiskLevel"

    test "every risk level has a non-empty description" (fun () ->
        for level in [ Safe; ProbablySafe; Review; Dangerous; NeverDelete ] do
            let d = RiskLevel.describe level
            assertTrue (d.Description.Length > 0) (sprintf "%A has a description" level)
            assertTrue (d.Icon.Length > 0) (sprintf "%A has an icon" level)
            assertTrue (d.AnsiColor.Length > 0) (sprintf "%A has a color" level))

    test "risk levels order from least to most dangerous" (fun () ->
        assertTrue (RiskLevel.severity Safe < RiskLevel.severity Review) "Safe is less severe than Review"
        assertTrue (RiskLevel.severity Review < RiskLevel.severity NeverDelete) "Review is less severe than NeverDelete")

    test "NeverDelete is the maximum severity" (fun () ->
        let all = [ Safe; ProbablySafe; Review; Dangerous; NeverDelete ]
        let maxLevel = all |> List.maxBy RiskLevel.severity
        assertEqual NeverDelete maxLevel "NeverDelete is most severe")

    test "parse accepts all valid names" (fun () ->
        let result1 = RiskLevel.parse "safe"
        assertEqual (Ok Safe) result1 "parse 'safe'"
        let result2 = RiskLevel.parse "probablysafe"
        assertEqual (Ok ProbablySafe) result2 "parse 'probablysafe'"
        let result3 = RiskLevel.parse "review"
        assertEqual (Ok Review) result3 "parse 'review'"
        let result4 = RiskLevel.parse "dangerous"
        assertEqual (Ok Dangerous) result4 "parse 'dangerous'"
        let result5 = RiskLevel.parse "neverdelete"
        assertEqual (Ok NeverDelete) result5 "parse 'neverdelete'")

    test "parse is case-insensitive and tolerates whitespace" (fun () ->
        let result1 = RiskLevel.parse "  SAFE  "
        assertEqual (Ok Safe) result1 "parse '  SAFE  '"
        let result2 = RiskLevel.parse "ReViEw"
        assertEqual (Ok Review) result2 "parse 'ReViEw'"
        let result3 = RiskLevel.parse "\t dangerous \n"
        assertEqual (Ok Dangerous) result3 "parse whitespace-padded dangerous")

    test "parse accepts hyphenated aliases" (fun () ->
        let result1 = RiskLevel.parse "probably-safe"
        assertEqual (Ok ProbablySafe) result1 "parse 'probably-safe'"
        let result2 = RiskLevel.parse "never-delete"
        assertEqual (Ok NeverDelete) result2 "parse 'never-delete'")

    test "parse returns error for unknown names" (fun () ->
        let error = assertError (RiskLevel.parse "unknown") "parse 'unknown' should error"
        assertContains "unknown" error "error message contains the input")

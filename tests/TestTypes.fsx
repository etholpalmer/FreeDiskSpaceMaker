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

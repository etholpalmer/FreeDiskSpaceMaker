(**
    Tests for command-line parsing: the two safety properties (scan is the
    default, --dry-run overrides --delete), error accumulation, and the
    help text's completeness.
*)
open TestFramework
open Cli

let run () =
    suite "Cli.parse"

    test "no arguments defaults to scan mode" (fun () ->
        // Dry run is the default and requires no flag. Running the tool with
        // no arguments must never be able to modify anything.
        let options = assertOk (parse []) "empty argument list"
        assertEqual Scan options.Mode "scan is the default"
        assertFalse options.Force "force is off by default")

    test "--scan selects scan mode" (fun () ->
        assertEqual Scan (assertOk (parse [ "--scan" ]) "scan flag").Mode "scan mode")

    test "--delete selects delete mode" (fun () ->
        assertEqual Delete (assertOk (parse [ "--delete" ]) "delete flag").Mode "delete mode")

    test "--dry-run forces scan mode even alongside --delete" (fun () ->
        // An explicit dry-run request must win over a delete request; the
        // safer interpretation of a contradictory command line is the correct one.
        let options = assertOk (parse [ "--delete"; "--dry-run" ]) "contradictory flags"
        assertEqual Scan options.Mode "dry-run wins")

    test "--help selects help mode" (fun () ->
        assertEqual Help (assertOk (parse [ "--help" ]) "help flag").Mode "help mode")

    test "report paths are captured" (fun () ->
        let options = assertOk (parse [ "--json"; "/tmp/r.json"; "--html"; "/tmp/r.html" ]) "report paths"
        assertEqual (Some "/tmp/r.json") options.JsonPath "json path"
        assertEqual (Some "/tmp/r.html") options.HtmlPath "html path"
        assertEqual None options.MarkdownPath "markdown not requested")

    test "numeric options are parsed" (fun () ->
        let options = assertOk (parse [ "--age"; "60"; "--min-size"; "2048"; "--threads"; "4" ]) "numeric options"
        assertEqual (Some 60) options.AgeDays "age"
        assertEqual (Some 2048L) options.MinSizeBytes "min size"
        assertEqual (Some 4) options.Threads "threads")

    test "boolean flags are captured" (fun () ->
        let options = assertOk (parse [ "--duplicates"; "--large-files"; "--verbose"; "--force" ]) "boolean flags"
        assertTrue options.Duplicates "duplicates"
        assertTrue options.LargeFiles "large files"
        assertTrue options.Verbose "verbose"
        assertTrue options.Force "force")

    test "an unknown flag is an error naming the flag" (fun () ->
        let errors = assertError (parse [ "--nonsense" ]) "unknown flag"
        assertContains "--nonsense" (List.head errors) "names the flag")

    test "a flag missing its value is an error" (fun () ->
        let errors = assertError (parse [ "--json" ]) "missing value"
        assertContains "--json" (List.head errors) "names the flag"
        assertContains "value" (List.head errors) "explains what is missing")

    test "a non-numeric value for a numeric flag is an error" (fun () ->
        let errors = assertError (parse [ "--threads"; "many" ]) "bad number"
        assertContains "many" (List.head errors) "names the offending value")

    test "every error is reported at once" (fun () ->
        let errors = assertError (parse [ "--nonsense"; "--threads"; "many" ]) "two problems"
        assertEqual 2 errors.Length "both errors reported")

    test "help text documents every supported flag" (fun () ->
        for flag in [ "--scan"; "--delete"; "--force"; "--dry-run"; "--json"; "--markdown"
                      "--html"; "--duplicates"; "--large-files"; "--age"; "--min-size"
                      "--threads"; "--hash-algorithm"; "--config"; "--allow-root"; "--verbose"; "--help" ] do
            assertContains flag helpText (sprintf "%s is documented" flag))

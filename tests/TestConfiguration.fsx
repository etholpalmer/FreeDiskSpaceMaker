(**
    Tests for Configuration: default merging, field parsing, error
    accumulation, and risk-override level parsing.
*)
open TestFramework
open Types
open Configuration

let run () =
    suite "Configuration.parse"

    test "an empty object yields the defaults" (fun () ->
        let c = assertOk (parse "{}") "empty object parses"
        assertEqual defaults.MinSizeBytes c.MinSizeBytes "default min size"
        assertEqual defaults.ThreadCount c.ThreadCount "default thread count")

    test "supplied values override the defaults" (fun () ->
        let json = """{"minSizeBytes": 2048, "ageThresholdDays": 60, "threadCount": 4}"""
        let c = assertOk (parse json) "parses"
        assertEqual 2048L c.MinSizeBytes "min size"
        assertEqual 60 c.AgeThresholdDays "age threshold"
        assertEqual 4 c.ThreadCount "thread count")

    test "ignore paths are expanded and normalized" (fun () ->
        let json = """{"ignorePaths": ["~/Projects/active", "/tmp//x/"]}"""
        let c = assertOk (parse json) "parses"
        assertEqual 2 c.IgnorePaths.Length "two paths"
        assertFalse (c.IgnorePaths |> List.exists (fun p -> p.StartsWith "~")) "tilde expanded"
        assertTrue (c.IgnorePaths |> List.contains "/tmp/x") "normalized")

    test "extensions are normalized to lowercase with a leading dot" (fun () ->
        let json = """{"ignoreExtensions": ["PSD", ".Sketch"]}"""
        let c = assertOk (parse json) "parses"
        assertEqual [ ".psd"; ".sketch" ] c.IgnoreExtensions "normalized extensions")

    test "malformed JSON reports an error rather than throwing" (fun () ->
        let errors = assertError (parse "{ not json") "malformed input"
        assertTrue (errors.Length >= 1) "at least one error"
        assertContains "JSON" (List.head errors) "mentions JSON")

    suite "Configuration validation"

    test "every validation error is reported at once" (fun () ->
        let json = """{"minSizeBytes": -1, "threadCount": 0, "ageThresholdDays": -5}"""
        let errors = assertError (parse json) "three invalid values"
        assertEqual 3 errors.Length "all three errors reported together")

    test "thread count above 64 is rejected" (fun () ->
        let errors = assertError (parse """{"threadCount": 128}""") "excessive thread count"
        assertContains "threadCount" (List.head errors) "names the field")

    test "an unknown hash algorithm is rejected" (fun () ->
        let errors = assertError (parse """{"hashAlgorithm": "crc32"}""") "unsupported algorithm"
        assertContains "hashAlgorithm" (List.head errors) "names the field")

    test "sha256 and md5 are both accepted" (fun () ->
        assertEqual "sha256" (assertOk (parse """{"hashAlgorithm": "SHA256"}""") "sha256").HashAlgorithm "sha256 normalized"
        assertEqual "md5" (assertOk (parse """{"hashAlgorithm": "MD5"}""") "md5").HashAlgorithm "md5 normalized")

    suite "Configuration risk overrides"

    test "an override that raises severity is accepted" (fun () ->
        let json = """{"riskOverrides": {"Homebrew Cache": "Review"}}"""
        let c = assertOk (parse json) "raising override accepted"
        assertEqual (Some Review) (c.RiskOverrides.TryFind "Homebrew Cache") "override recorded")

    test "an override to NeverDelete is accepted" (fun () ->
        let json = """{"riskOverrides": {"Xcode DerivedData": "NeverDelete"}}"""
        let c = assertOk (parse json) "maximum severity accepted"
        assertEqual (Some NeverDelete) (c.RiskOverrides.TryFind "Xcode DerivedData") "override recorded")

    test "an unknown risk level name is rejected" (fun () ->
        let errors = assertError (parse """{"riskOverrides": {"Some Rule": "Whatever"}}""") "bad level"
        assertContains "Whatever" (List.head errors) "names the offending value")

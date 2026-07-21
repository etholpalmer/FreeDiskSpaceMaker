(**
    Tests for Utilities: Format, Path, and Result. No #load directive — this
    file is loaded by Tests.fsx, which owns the canonical load order.
*)
open System
open TestFramework
open Utilities

let run () =
    suite "Utilities.Format"

    test "bytes renders binary units with one decimal" (fun () ->
        assertEqual "0 B" (Format.bytes 0L) "zero"
        assertEqual "512 B" (Format.bytes 512L) "sub-kilobyte"
        assertEqual "1.0 KB" (Format.bytes 1024L) "exactly one kilobyte"
        assertEqual "1.5 KB" (Format.bytes 1536L) "one and a half kilobytes"
        assertEqual "1.0 MB" (Format.bytes 1048576L) "one megabyte"
        assertEqual "2.5 GB" (Format.bytes 2684354560L) "two and a half gigabytes"
        assertEqual "1.0 TB" (Format.bytes 1099511627776L) "one terabyte")

    test "bytes handles negative values" (fun () ->
        assertEqual "-1.0 KB" (Format.bytes -1024L) "negative kilobyte")

    test "duration renders compactly" (fun () ->
        assertEqual "450ms" (Format.duration (TimeSpan.FromMilliseconds 450.0)) "sub-second"
        assertEqual "1.5s" (Format.duration (TimeSpan.FromSeconds 1.5)) "seconds"
        assertEqual "2m 5s" (Format.duration (TimeSpan.FromSeconds 125.0)) "minutes"
        assertEqual "1h 1m" (Format.duration (TimeSpan.FromMinutes 61.0)) "hours")

    suite "Utilities.Path"

    test "normalize collapses separators and trailing slashes" (fun () ->
        assertEqual "/a/b/c" (Path.normalize "/a/b/c/") "trailing slash removed"
        assertEqual "/a/b/c" (Path.normalize "/a//b///c") "duplicate separators collapsed"
        assertEqual "/" (Path.normalize "/") "root preserved")

    test "normalize resolves dot and dot-dot segments" (fun () ->
        assertEqual "/a/c" (Path.normalize "/a/b/../c") "dot-dot resolved"
        assertEqual "/a/b" (Path.normalize "/a/./b") "dot removed"
        assertEqual "/" (Path.normalize "/a/..") "dot-dot to root")

    test "normalize applies Unicode NFC so macOS NFD paths compare equal" (fun () ->
        // macOS stores filenames decomposed (NFD); user config supplies composed (NFC).
        // Built from explicit escapes (not typed characters) so the two literals are
        // guaranteed to contain genuinely different byte sequences regardless of how
        // the editor/filesystem stores this source file.
        let composed = "/Users/e/Café"        // e-acute as a single codepoint U+00E9
        let decomposed = "/Users/e/Café"      // e + combining acute accent U+0301
        assertEqual (Path.normalize composed) (Path.normalize decomposed) "NFC and NFD normalize identically")

    test "isDescendantOf requires a proper segment boundary" (fun () ->
        assertTrue (Path.isDescendantOf "/a/b" "/a/b/c") "direct child"
        assertTrue (Path.isDescendantOf "/a/b" "/a/b/c/d") "deeper descendant"
        assertFalse (Path.isDescendantOf "/a/b" "/a/b") "a path is not its own descendant"
        assertFalse (Path.isDescendantOf "/a/b" "/a/bc") "prefix match is not a descendant"
        assertFalse (Path.isDescendantOf "/a/b" "/a") "parent is not a descendant")

    test "isDescendantOf rejects traversal escapes" (fun () ->
        assertFalse (Path.isDescendantOf "/a/b" "/a/b/../../etc/passwd") "dot-dot escape rejected"
        assertTrue (Path.isDescendantOf "/a/b" "/a/b/c/../d") "dot-dot staying inside is allowed")

    test "segments splits a path into its components" (fun () ->
        assertEqual [ "a"; "b"; "c" ] (Path.segments "/a/b/c") "absolute path segments"
        assertEqual ([]: string list) (Path.segments "/") "root has no segments")

    let isWindows =
        Runtime.InteropServices.RuntimeInformation.IsOSPlatform Runtime.InteropServices.OSPlatform.Windows

    if not isWindows then
        test "segments preserves a backslash inside a filename on POSIX" (fun () ->
            let result = Path.segments "/tmp/back\\slash"
            assertEqual 2 result.Length "backslash is not a separator on POSIX"
            assertEqual "back\\slash" (List.last result) "backslash stays part of the segment")

        test "normalize does not treat a `..` inside a filename as a parent reference on POSIX" (fun () ->
            let result = Path.normalize "/allow/root/x\\../secret"
            assertFalse (result = "/allow/root/secret") "x\\.. must not collapse to a traversal"
            assertContains "x\\.." result "the literal directory name is retained")

        test "isDescendantOf keeps the literal component rather than popping a segment on POSIX" (fun () ->
            assertTrue (Path.isDescendantOf "/allow/root" "/allow/root/x\\../secret") "genuinely inside, since x\\.. is one directory name"
            let normalized = Path.normalize "/allow/root/x\\../secret"
            assertContains "x\\.." normalized "no segment was popped by a fake dot-dot")

    test "isDescendantOf still rejects a genuine traversal escape" (fun () ->
        assertFalse (Path.isDescendantOf "/a/b" "/a/b/../../etc/passwd") "real dot-dot segments still escape")

    suite "Utilities.Result"

    test "traverse collects all errors rather than stopping at the first" (fun () ->
        let f (n: int) = if n % 2 = 0 then Ok n else Error (sprintf "%d is odd" n)
        let result = Result.traverse f [ 1; 2; 3 ]
        let errors = assertError result "odd inputs produce errors"
        assertEqual [ "1 is odd"; "3 is odd" ] errors "both errors collected")

    test "traverse returns all values when every element succeeds" (fun () ->
        let f (n: int) = Ok (n * 2)
        let values = assertOk (Result.traverse f [ 1; 2; 3 ]) "all succeed"
        assertEqual [ 2; 4; 6 ] values "values in order")

    suite "Utilities.Glob"

    test "star matches within a single segment only" (fun () ->
        assertTrue (Glob.matches "*.log" "app.log") "star matches a basename"
        assertTrue (Glob.matches "*.log" "a.log") "star matches short names"
        assertFalse (Glob.matches "*.log" "dir/app.log") "star does not cross a separator"
        assertFalse (Glob.matches "*.log" "app.txt") "extension must match")

    test "question mark matches exactly one character" (fun () ->
        assertTrue (Glob.matches "a?c" "abc") "single character"
        assertFalse (Glob.matches "a?c" "ac") "zero characters does not match"
        assertFalse (Glob.matches "a?c" "abbc") "two characters does not match"
        assertFalse (Glob.matches "a?c" "a/c") "question mark does not match a separator")

    test "double star crosses separators" (fun () ->
        assertTrue (Glob.matches "**/*.log" "a/b/c.log") "deep match"
        assertTrue (Glob.matches "**/*.log" "c.log") "double star matches zero directories"
        assertTrue (Glob.matches "Containers/**" "Containers/a/b/c") "trailing double star"
        assertTrue (Glob.matches "**" "anything/at/all") "bare double star matches everything")

    test "literal patterns match exactly" (fun () ->
        assertTrue (Glob.matches "DerivedData" "DerivedData") "exact literal"
        assertFalse (Glob.matches "DerivedData" "DerivedDataX") "no implicit prefix match")

    test "matching is case-insensitive on macOS and Windows" (fun () ->
        // APFS is case-insensitive by default, so a rule written as "Caches"
        // must still match a directory reported as "caches".
        if Path.platformIsCaseInsensitive then
            assertTrue (Glob.matches "*.LOG" "app.log") "case-insensitive on this platform"
        else
            assertFalse (Glob.matches "*.LOG" "app.log") "case-sensitive on this platform")

    test "regex metacharacters in patterns are treated literally" (fun () ->
        assertTrue (Glob.matches "a.b" "a.b") "dot is literal"
        assertFalse (Glob.matches "a.b" "axb") "dot does not match any character"
        assertTrue (Glob.matches "v1.0+build" "v1.0+build") "plus is literal")

    test "matchesAny succeeds when any pattern matches" (fun () ->
        assertTrue (Glob.matchesAny [ "*.tmp"; "*.log" ] "app.log") "second pattern matches"
        assertFalse (Glob.matchesAny [ "*.tmp"; "*.log" ] "app.txt") "no pattern matches"
        assertFalse (Glob.matchesAny [] "app.log") "empty pattern list matches nothing")

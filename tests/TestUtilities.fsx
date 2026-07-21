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

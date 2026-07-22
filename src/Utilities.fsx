(**
    Shared, dependency-light helpers: human-readable formatting, path
    normalization, and Result combinators.

    Path normalization is security-relevant. `isDescendantOf` is the primitive
    the risk engine's allowlist gate is built on, so it must reject prefix
    collisions ("/a/bc" is not inside "/a/b") and traversal escapes.
*)
module Utilities

open System
open System.Globalization
open System.Text

/// Human-readable rendering of sizes, durations, and rates.
module Format =

    let private units = [| "B"; "KB"; "MB"; "GB"; "TB"; "PB" |]

    /// Renders a byte count using binary units, one decimal place above 1 KB.
    let bytes (count: int64) : string =
        let sign = if count < 0L then "-" else ""
        let mutable value = abs (float count)
        let mutable index = 0
        while value >= 1024.0 && index < units.Length - 1 do
            value <- value / 1024.0
            index <- index + 1
        if index = 0 then sprintf "%s%d %s" sign (int value) units.[index]
        else sprintf "%s%.1f %s" sign value units.[index]

    /// Renders a duration compactly, choosing units by magnitude.
    let duration (span: TimeSpan) : string =
        if span.TotalSeconds < 1.0 then sprintf "%dms" (int span.TotalMilliseconds)
        elif span.TotalMinutes < 1.0 then sprintf "%.1fs" span.TotalSeconds
        elif span.TotalHours < 1.0 then sprintf "%dm %ds" span.Minutes span.Seconds
        else sprintf "%dh %dm" (int span.TotalHours) span.Minutes

    /// Renders a per-second rate with a caller-supplied unit label.
    let rate (perSecond: float) (unit: string) : string =
        if Double.IsNaN perSecond || Double.IsInfinity perSecond then sprintf "— %s/s" unit
        elif perSecond >= 1000.0 then sprintf "%s %s/s" (perSecond.ToString("N0", CultureInfo.InvariantCulture)) unit
        else sprintf "%.1f %s/s" perSecond unit

/// Path handling. Every comparison in this module is normalization-first.
module Path =

    let private separator = IO.Path.DirectorySeparatorChar

    /// True when the running platform compares paths case-insensitively.
    /// macOS APFS is case-insensitive by default; Windows always is.
    let platformIsCaseInsensitive =
        Runtime.InteropServices.RuntimeInformation.IsOSPlatform Runtime.InteropServices.OSPlatform.OSX
        || Runtime.InteropServices.RuntimeInformation.IsOSPlatform Runtime.InteropServices.OSPlatform.Windows

    /// True when the running platform treats `\` as a path separator.
    /// Windows does; macOS and Linux treat `\` as an ordinary filename character.
    let private isWindows =
        Runtime.InteropServices.RuntimeInformation.IsOSPlatform Runtime.InteropServices.OSPlatform.Windows

    let private separatorChars =
        if isWindows then [| '/'; '\\' |] else [| '/' |]

    /// Splits a path into its non-empty segments. On Windows, both `/` and
    /// `\` separate segments; on macOS and Linux, only `/` does, since `\`
    /// is a legal filename character on those platforms.
    let segments (path: string) : string list =
        path.Split(separatorChars, StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    /// True when `path` begins with a Windows drive letter (`C:\` or `D:/`).
    /// Checked independent of the running platform, since it is a syntactic
    /// property of the path string rather than a platform behavior.
    let private hasDriveLetter (path: string) : bool =
        path.Length >= 2
        && Char.IsLetter path.[0]
        && path.[1] = ':'
        && (path.Length = 2 || path.[2] = '/' || path.[2] = '\\')

    /// Normalizes a path for comparison: resolves `.` and `..`, collapses
    /// duplicate separators, removes any trailing separator, and applies
    /// Unicode NFC. The NFC step matters on macOS, which stores filenames
    /// decomposed (NFD) while configuration files supply composed (NFC) text.
    let normalize (path: string) : string =
        if String.IsNullOrWhiteSpace path then ""
        else
            let hasDrive = hasDriveLetter path
            let isAbsolute =
                hasDrive
                || (path.Length > 0 && Array.contains path.[0] separatorChars)
            let resolved =
                segments path
                |> List.fold (fun acc segment ->
                    match segment with
                    | "." -> acc
                    | ".." -> (match acc with [] -> [] | _ :: rest -> rest)
                    | s -> s :: acc) []
                |> List.rev
            let joined = String.Join(string separator, resolved)
            let withRoot =
                if hasDrive then joined
                elif isAbsolute then string separator + joined
                else joined
            let trimmed =
                if withRoot.Length > 1 && withRoot.EndsWith(string separator)
                then withRoot.TrimEnd separator
                else withRoot
            trimmed.Normalize NormalizationForm.FormC

    /// Expands a leading `~` to the current user's home directory.
    let expandHome (path: string) : string =
        if path = "~" || path.StartsWith "~/" || path.StartsWith "~\\" then
            let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
            IO.Path.Combine(home, path.Substring(min 2 path.Length))
        else path

    /// Compares two normalized paths honoring platform case sensitivity.
    let equals (a: string) (b: string) : bool =
        let comparison =
            if platformIsCaseInsensitive then StringComparison.OrdinalIgnoreCase
            else StringComparison.Ordinal
        String.Equals(normalize a, normalize b, comparison)

    /// True when `candidate` is a strict descendant of `ancestor`.
    ///
    /// Both paths are normalized first, so `..` escapes are resolved before
    /// comparison rather than being string-matched. A path is never its own
    /// descendant, and a shared textual prefix that does not end on a segment
    /// boundary ("/a/bc" against "/a/b") is not a descendant.
    let isDescendantOf (ancestor: string) (candidate: string) : bool =
        let a = normalize ancestor
        let c = normalize candidate
        let comparison =
            if platformIsCaseInsensitive then StringComparison.OrdinalIgnoreCase
            else StringComparison.Ordinal
        if a = "" || c = "" then false
        elif String.Equals(a, c, comparison) then false
        else
            let prefix = if a.EndsWith(string separator) then a else a + string separator
            c.StartsWith(prefix, comparison)

/// Combinators over Result that accumulate errors rather than short-circuiting.
module Result =

    /// Applies a fallible function to every element, collecting **all** errors.
    /// Configuration validation reports every problem at once rather than
    /// making the user fix them one run at a time.
    let traverse (f: 'a -> Result<'b, 'e>) (items: 'a list) : Result<'b list, 'e list> =
        let results = items |> List.map f
        let errors = results |> List.choose (function Error e -> Some e | Ok _ -> None)
        if List.isEmpty errors then
            Ok (results |> List.choose (function Ok v -> Some v | Error _ -> None))
        else Error errors

    /// Converts an exception-throwing thunk into a Result.
    let protect (onError: exn -> 'e) (f: unit -> 'a) : Result<'a, 'e> =
        try Ok (f ()) with ex -> Error (onError ex)

/// Glob matching for rule include/exclude patterns.
///
/// Patterns are translated to anchored regular expressions and cached. The
/// translation escapes every regex metacharacter, so a pattern like `v1.0+build`
/// matches literally rather than being interpreted as a regex.
module Glob =

    open System.Collections.Concurrent
    open System.Text.RegularExpressions

    // The cache is unbounded, which is safe only because every caller passes
    // patterns drawn from the fixed, bounded rule catalog (see Task 7's rule
    // catalog). If patterns were ever constructed per-file (e.g. embedding a
    // filename into a pattern string), this cache would become an unbounded
    // memory-growth vector and would need eviction or a bounded size.
    let private cache = ConcurrentDictionary<string * bool, Regex>()

    /// Regex match timeout applied to every compiled pattern. A few seconds
    /// is ample for any legitimate glob against a filesystem path; it exists
    /// purely as defense in depth against pathological chained `**` patterns
    /// that could otherwise backtrack for a very long time.
    let private matchTimeout = TimeSpan.FromSeconds 3.0

    /// Translates a glob pattern into an anchored regex source string.
    ///
    /// `**` crosses separators (and matches zero of them, so `**/x` matches a
    /// bare `x`); `*` and `?` do not cross separators. Consecutive `**`
    /// segments (e.g. `**/**/x`) are collapsed into a single crossing group,
    /// since they are semantically identical to one `**/` and chaining them
    /// otherwise causes exponential backtracking against long non-matching
    /// paths.
    ///
    /// Anchors are `\A` and `\z` rather than `^` and `$`: in .NET, `$` matches
    /// at end-of-string OR immediately before a trailing `\n`, and macOS
    /// permits filenames ending in a newline, so `^x$` would wrongly match a
    /// file named `x\n`. `\A`/`\z` anchor to the true string boundaries.
    let private translate (pattern: string) : string =
        let sb = StringBuilder()
        sb.Append @"\A" |> ignore
        let mutable i = 0
        let mutable pendingDoubleStarGroup = false
        let flushPending () =
            if pendingDoubleStarGroup then
                sb.Append "(?:.*[/\\\\])?" |> ignore
                pendingDoubleStarGroup <- false
        while i < pattern.Length do
            match pattern.[i] with
            | '*' when i + 1 < pattern.Length && pattern.[i + 1] = '*' ->
                // `**/` should also match zero directories, so consume an
                // optional following separator as part of the same group.
                // Consecutive `**/` runs collapse into one pending group
                // instead of emitting a fresh group per occurrence.
                if i + 2 < pattern.Length && (pattern.[i + 2] = '/' || pattern.[i + 2] = '\\') then
                    pendingDoubleStarGroup <- true
                    i <- i + 3
                else
                    flushPending ()
                    sb.Append ".*" |> ignore
                    i <- i + 2
            | '*' ->
                flushPending ()
                sb.Append "[^/\\\\]*" |> ignore
                i <- i + 1
            | '?' ->
                flushPending ()
                sb.Append "[^/\\\\]" |> ignore
                i <- i + 1
            | '/' | '\\' ->
                flushPending ()
                sb.Append "[/\\\\]" |> ignore
                i <- i + 1
            | c ->
                flushPending ()
                sb.Append(Regex.Escape(string c)) |> ignore
                i <- i + 1
        flushPending ()
        sb.Append @"\z" |> ignore
        sb.ToString()

    /// Compiles and caches the regex for a pattern.
    let private regexFor (pattern: string) : Regex =
        let key = (pattern, Path.platformIsCaseInsensitive)
        cache.GetOrAdd(key, fun (p, insensitive) ->
            let options =
                if insensitive then RegexOptions.Compiled ||| RegexOptions.IgnoreCase
                else RegexOptions.Compiled
            Regex(translate p, options, matchTimeout))

    /// True when `path` matches `pattern`. Case sensitivity follows the platform.
    /// A regex match timeout (defense in depth against pathological patterns)
    /// is treated as "does not match" — a timeout must never cause a file to
    /// be treated as matching a delete rule.
    let matches (pattern: string) (path: string) : bool =
        try
            (regexFor pattern).IsMatch path
        with :? RegexMatchTimeoutException ->
            false

    /// True when `path` matches any of the supplied patterns.
    /// An empty pattern list matches nothing.
    let matchesAny (patterns: string list) (path: string) : bool =
        patterns |> List.exists (fun p -> matches p path)

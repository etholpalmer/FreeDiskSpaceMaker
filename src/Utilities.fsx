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

    /// Splits a path into its non-empty segments.
    let segments (path: string) : string list =
        path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    /// Normalizes a path for comparison: resolves `.` and `..`, collapses
    /// duplicate separators, removes any trailing separator, and applies
    /// Unicode NFC. The NFC step matters on macOS, which stores filenames
    /// decomposed (NFD) while configuration files supply composed (NFC) text.
    let normalize (path: string) : string =
        if String.IsNullOrWhiteSpace path then ""
        else
            let isAbsolute = path.StartsWith "/" || path.StartsWith "\\"
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
                if isAbsolute then string separator + joined
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
        if a = "" || c = "" then false
        elif equals a c then false
        else
            let comparison =
                if platformIsCaseInsensitive then StringComparison.OrdinalIgnoreCase
                else StringComparison.Ordinal
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

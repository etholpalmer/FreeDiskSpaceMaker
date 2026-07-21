(**
    Command-line parsing.

    Hand-rolled rather than taking a dependency: the surface is small and the
    project forbids third-party packages.

    Two safety properties are enforced here. Scan is the default mode, so
    invoking the tool with no arguments cannot modify anything. And --dry-run
    overrides --delete, so the safer reading of a contradictory command line
    always wins.

    Errors accumulate: a command line with three mistakes reports all three.
*)
module Cli

open System

/// What the tool was asked to do.
type Mode =
    | Scan
    | Delete
    | Help

/// Parsed command-line options.
type Options =
    { Mode: Mode
      JsonPath: string option
      MarkdownPath: string option
      HtmlPath: string option
      ConfigPath: string option
      Duplicates: bool
      LargeFiles: bool
      AgeDays: int option
      MinSizeBytes: int64 option
      Threads: int option
      HashAlgorithm: string option
      /// Widens the risk gate to include ProbablySafe. Never bypasses the veto.
      Force: bool
      AllowRoot: bool
      Verbose: bool }

/// Options with nothing requested: a plain dry-run scan.
let private empty: Options =
    { Mode = Scan
      JsonPath = None
      MarkdownPath = None
      HtmlPath = None
      ConfigPath = None
      Duplicates = false
      LargeFiles = false
      AgeDays = None
      MinSizeBytes = None
      Threads = None
      HashAlgorithm = None
      Force = false
      AllowRoot = false
      Verbose = false }

/// Usage text. Every flag the parser accepts must appear here; a test enforces it.
let helpText =
    """DiskCleanupAdvisor — find and safely reclaim disk space

USAGE
    dotnet fsi DiskCleanupAdvisor.fsx [options]

MODES
    --scan                  Scan and report. This is the default.
    --delete                Move Safe-rated items to Trash, after confirmation.
    --dry-run               Force scan mode. Overrides --delete.
    --help                  Show this text.

REPORTS
    --json <path>           Write a JSON report.
    --markdown <path>       Write a Markdown report.
    --html <path>           Write a self-contained HTML report.

SCAN OPTIONS
    --duplicates            Detect duplicate files.
    --large-files           Report the largest files.
    --age <days>            Only consider items older than this.
    --min-size <bytes>      Size floor for hashing and large-file reporting.
    --threads <n>           Traversal parallelism.
    --hash-algorithm <alg>  sha256 (default) or md5.
    --config <path>         Configuration file.

SAFETY
    --force                 Also permit ProbablySafe items. Never bypasses the
                            protected-path veto, which has no override.
    --allow-root            Permit running with elevated privileges.
    --verbose               Debug logging to stderr.

NOTES
    Nothing is deleted without --delete and an interactive confirmation.
    Deletion moves items to the Trash; it never unlinks. Space is reclaimed
    only when you empty the Trash yourself."""

/// Parses arguments, accumulating every error.
let parse (args: string list) : Result<Options, string list> =
    let errors = ResizeArray<string>()

    /// Reads the value following a flag, recording an error when absent.
    let readValue (flag: string) (rest: string list) : string option * string list =
        match rest with
        | value :: remaining when not (value.StartsWith "--") -> Some value, remaining
        | _ ->
            errors.Add(sprintf "%s requires a value" flag)
            None, rest

    let parseInt (flag: string) (text: string) : int option =
        match Int32.TryParse text with
        | true, n -> Some n
        | _ ->
            errors.Add(sprintf "%s expects a number, got '%s'" flag text)
            None

    let parseInt64 (flag: string) (text: string) : int64 option =
        match Int64.TryParse text with
        | true, n -> Some n
        | _ ->
            errors.Add(sprintf "%s expects a number, got '%s'" flag text)
            None

    // Tracked separately so --dry-run can override --delete regardless of order.
    let mutable explicitDryRun = false

    let rec loop (options: Options) (remaining: string list) : Options =
        match remaining with
        | [] -> options
        | flag :: rest ->
            match flag with
            | "--scan" -> loop { options with Mode = Scan } rest
            | "--delete" -> loop { options with Mode = Delete } rest
            | "--dry-run" ->
                explicitDryRun <- true
                loop options rest
            | "--help" | "-h" -> loop { options with Mode = Help } rest
            | "--duplicates" -> loop { options with Duplicates = true } rest
            | "--large-files" -> loop { options with LargeFiles = true } rest
            | "--force" -> loop { options with Force = true } rest
            | "--allow-root" -> loop { options with AllowRoot = true } rest
            | "--verbose" | "-v" -> loop { options with Verbose = true } rest
            | "--json" ->
                let value, next = readValue flag rest
                loop { options with JsonPath = value } next
            | "--markdown" ->
                let value, next = readValue flag rest
                loop { options with MarkdownPath = value } next
            | "--html" ->
                let value, next = readValue flag rest
                loop { options with HtmlPath = value } next
            | "--config" ->
                let value, next = readValue flag rest
                loop { options with ConfigPath = value } next
            | "--hash-algorithm" ->
                let value, next = readValue flag rest
                loop { options with HashAlgorithm = value |> Option.map (fun v -> v.ToLowerInvariant()) } next
            | "--age" ->
                let value, next = readValue flag rest
                loop { options with AgeDays = value |> Option.bind (parseInt flag) } next
            | "--min-size" ->
                let value, next = readValue flag rest
                loop { options with MinSizeBytes = value |> Option.bind (parseInt64 flag) } next
            | "--threads" ->
                let value, next = readValue flag rest
                loop { options with Threads = value |> Option.bind (parseInt flag) } next
            | unknown ->
                errors.Add(sprintf "unknown option '%s' (try --help)" unknown)
                loop options rest

    let parsed = loop empty args

    // An explicit dry-run request always wins over a delete request.
    let final =
        if explicitDryRun && parsed.Mode = Delete then { parsed with Mode = Scan } else parsed

    if errors.Count > 0 then Error (List.ofSeq errors) else Ok final

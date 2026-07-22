(**
    DiskCleanupAdvisor — entry point.

    Finds disposable files, explains why each is disposable, and — only when
    explicitly asked — moves them to the Trash. Never unlinks.

    Usage:  dotnet fsi DiskCleanupAdvisor.fsx -- [options]
            dotnet fsi DiskCleanupAdvisor.fsx -- --help

    This file is composition only. All behavior lives in src/.
*)

#load "src/Types.fsx"
#load "src/Utilities.fsx"
#load "src/Logging.fsx"
#load "src/Diagnostics.fsx"
#load "src/Platform.fsx"
#load "src/Configuration.fsx"
#load "src/Rules.fsx"
#load "src/RiskEngine.fsx"
#load "src/FileClassifier.fsx"
#load "src/DirectoryWalker.fsx"
#load "src/Hashing.fsx"
#load "src/Duplicates.fsx"
#load "src/Scanner.fsx"
#load "src/Report/Dto.fsx"
#load "src/Report/Json.fsx"
#load "src/Snapshots.fsx"
#load "src/Report/Console.fsx"
#load "src/Report/Markdown.fsx"
#load "src/Report/Html.fsx"
#load "src/Cli.fsx"
#load "src/Cleaner.fsx"

open System

/// Prints a list of errors to stderr and returns a failure exit code.
let private fail (errors: string list) : int =
    eprintfn "Error:"
    for e in errors do eprintfn "  %s" e
    1

/// Applies command-line overrides on top of the loaded configuration.
/// The command line wins over the file, which wins over the defaults.
let private mergeOptions (options: Cli.Options) (config: Configuration.Config) : Configuration.Config =
    { config with
        AgeThresholdDays = defaultArg options.AgeDays config.AgeThresholdDays
        FindingsMinSizeBytes = defaultArg options.MinSizeBytes config.FindingsMinSizeBytes
        HashAlgorithm = defaultArg options.HashAlgorithm config.HashAlgorithm }

/// Runs a scan and emits every requested report.
let private runScan (options: Cli.Options) (config: Configuration.Config) (log: Logging.Logger) : int =
    let fs = Platform.FileSystem.real
    let platform = Platform.current ()

    log |> Logging.info (sprintf "starting scan on %A" platform)

    let rules = Rules.resolveAll fs platform
    log |> Logging.info (sprintf "%d rules resolved on this machine" rules.Length)

    if List.isEmpty rules then
        eprintfn "No rules resolved on this machine. Nothing to scan."
        0
    else

    match Scanner.scan fs config options.Force options.Duplicates rules with
    | Error errors -> fail errors
    | Ok result ->
        log |> Logging.info (sprintf "scan complete: %d findings" result.Findings.Length)

        let snapshots =
            match Snapshots.list () with
            | Ok found -> found
            | Error message ->
                log |> Logging.warn message
                []

        // Console output always.
        printf "%s" (Report.Console.render snapshots result options.LargeFiles)

        // File reports on request. A write failure is reported but does not
        // discard the scan that already succeeded.
        let writeFailures =
            [ match options.JsonPath with
              | Some path ->
                  match Report.Json.writeWith options.JsonFull path result with
                  | Ok () -> log |> Logging.info (sprintf "wrote JSON report to %s" path)
                  | Error e -> yield e
              | None -> ()
              match options.MarkdownPath with
              | Some path ->
                  match Report.Markdown.write path result with
                  | Ok () -> log |> Logging.info (sprintf "wrote Markdown report to %s" path)
                  | Error e -> yield e
              | None -> ()
              match options.HtmlPath with
              | Some path ->
                  match Report.Html.write path result with
                  | Ok () -> log |> Logging.info (sprintf "wrote HTML report to %s" path)
                  | Error e -> yield e
              | None -> () ]

        if List.isEmpty writeFailures then 0 else fail writeFailures

/// Entry point. `fsi.CommandLineArgs` includes the script name at index 0.
let private main () : int =
    let args = fsi.CommandLineArgs |> Array.toList |> List.skip 1

    match Cli.parse args with
    | Error errors ->
        eprintfn "%s" Cli.helpText
        eprintfn ""
        fail errors
    | Ok options ->

    match options.Mode with
    | Cli.Help ->
        printfn "%s" Cli.helpText
        0
    | Cli.Delete ->
        let log = Logging.create (if options.Verbose then Logging.Debug else Logging.Info)
        if Platform.isElevated () && not options.AllowRoot then
            fail [ "refusing to run with elevated privileges; pass --allow-root to override" ]
        else
        match Configuration.load Platform.FileSystem.real options.ConfigPath with
        | Error errors -> fail errors
        | Ok config ->
            let config = mergeOptions options config
            let fs = Platform.FileSystem.real
            let rules = Rules.resolveAll fs (Platform.current ())
            match Scanner.scan fs config options.Force options.Duplicates rules with
            | Error errors -> fail errors
            | Ok result ->
                let snapshots =
                    match Snapshots.list () with
                    | Ok found -> found
                    | Error message ->
                        log |> Logging.warn message
                        []
                printf "%s" (Report.Console.render snapshots result options.LargeFiles)
                let candidates = result.Findings |> List.choose (fun f -> f.Candidate)
                if List.isEmpty candidates then
                    printfn "Nothing is eligible for automatic removal."
                    0
                else
                    let total = candidates |> List.sumBy RiskEngine.candidateSize
                    printfn ""
                    printfn "About to move %d items (%s) to the Trash."
                        candidates.Length (Utilities.Format.bytes total)
                    printfn "They can be restored from the Trash until you empty it."
                    printf  "Type 'yes' to continue: "
                    match Console.ReadLine() with
                    | "yes" ->
                        // One timestamp ties the journal, the Trash run folder
                        // and the restore script together.
                        let stamp = DateTime.Now
                        let journal = Cleaner.journalPath stamp
                        match Cleaner.writeJournal journal candidates with
                        | Error e -> fail [ e ]
                        | Ok () ->
                            // Written before the move, like the journal: if the
                            // run dies partway, the script still restores
                            // whatever did make it into the Trash.
                            let restoreScript = Cleaner.restoreScriptPath stamp
                            match Cleaner.trashRoot () with
                            | Ok root ->
                                match Cleaner.writeRestoreScript restoreScript root stamp candidates with
                                | Ok () -> log |> Logging.info (sprintf "wrote restore script to %s" restoreScript)
                                | Error e -> log |> Logging.warn e
                            | Error e -> log |> Logging.warn e
                            let outcome = Cleaner.moveToTrash fs log stamp candidates
                            printfn ""
                            printfn "Moved %d items (%s) to the Trash."
                                outcome.Moved.Length (Utilities.Format.bytes outcome.BytesMoved)
                            // Stated plainly: the space is not back yet.
                            printfn "Empty the Trash to actually reclaim this space."
                            printfn "To undo this run:  sh %s" (Cleaner.restoreScriptPath stamp)
                            if not (List.isEmpty outcome.Failed) then
                                printfn "%d items could not be moved; see %s" outcome.Failed.Length journal
                            0
                    | _ ->
                        printfn "Cancelled. Nothing was modified."
                        0
    | Cli.Scan ->
        let log =
            Logging.create (if options.Verbose then Logging.Debug else Logging.Info)

        if Platform.isElevated () && not options.AllowRoot then
            fail [ "refusing to run with elevated privileges; pass --allow-root to override" ]
        else
            match Configuration.load Platform.FileSystem.real options.ConfigPath with
            | Error errors -> fail errors
            | Ok config -> runScan options (mergeOptions options config) log

exit (main ())

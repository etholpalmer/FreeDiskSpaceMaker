(**
    Leveled structured logging.

    All log output goes to **stderr** so that stdout carries only the report.
    This keeps `dotnet fsi DiskCleanupAdvisor.fsx --json /dev/stdout` usable in
    a pipeline without log lines corrupting the JSON.
*)
module Logging

open System

/// Log severity, ordered from most to least verbose.
type Level =
    | Debug
    | Info
    | Warn
    | Error

module Level =
    /// Numeric rank used for threshold comparison.
    let rank (level: Level) : int =
        match level with
        | Debug -> 0
        | Info -> 1
        | Warn -> 2
        | Error -> 3

    /// Fixed-width label for aligned output.
    let label (level: Level) : string =
        match level with
        | Debug -> "DEBUG"
        | Info -> "INFO "
        | Warn -> "WARN "
        | Error -> "ERROR"

    /// ANSI color for the level label.
    let color (level: Level) : string =
        match level with
        | Debug -> "\027[90m"
        | Info -> "\027[36m"
        | Warn -> "\027[33m"
        | Error -> "\027[31m"

/// A logger bound to a minimum severity threshold.
type Logger =
    { Threshold: Level
      UseColor: bool }

/// True when color output is appropriate: stderr is not redirected and
/// the NO_COLOR convention has not been set.
let colorSupported () : bool =
    not (Console.IsErrorRedirected)
    && String.IsNullOrEmpty(Environment.GetEnvironmentVariable "NO_COLOR")

/// Creates a logger that emits records at or above the given level.
let create (threshold: Level) : Logger =
    { Threshold = threshold; UseColor = colorSupported () }

/// True when the logger would emit a record at this level.
let isEnabled (level: Level) (logger: Logger) : bool =
    Level.rank level >= Level.rank logger.Threshold

/// Writes one record to stderr if the level meets the threshold.
let log (level: Level) (message: string) (logger: Logger) : unit =
    if isEnabled level logger then
        let stamp = DateTime.Now.ToString "HH:mm:ss.fff"
        if logger.UseColor then
            eprintfn "\027[90m%s\027[0m %s%s\027[0m %s" stamp (Level.color level) (Level.label level) message
        else
            eprintfn "%s %s %s" stamp (Level.label level) message

/// Writes a debug-level record.
let debug (message: string) (logger: Logger) : unit = log Debug message logger

/// Writes an info-level record.
let info (message: string) (logger: Logger) : unit = log Info message logger

/// Writes a warning-level record.
let warn (message: string) (logger: Logger) : unit = log Warn message logger

/// Writes an error-level record.
let error (message: string) (logger: Logger) : unit = log Error message logger

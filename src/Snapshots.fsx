(**
    Time Machine local snapshot reporting (macOS only).

    Local snapshots frequently consume more space than everything this tool can
    remove combined, and no file deletion reclaims them. Reporting caches
    without mentioning snapshots would leave a user confused about why their
    free space did not change.

    This module only reports. Removing a snapshot is a system-level operation
    with its own consequences, so the tool prints the command and lets the user
    decide.
*)
module Snapshots

open System
open System.Diagnostics

/// One local Time Machine snapshot.
type LocalSnapshot =
    { Name: string
      CreatedUtc: DateTime option }

/// Parses the timestamp embedded in a snapshot name such as
/// `com.apple.TimeMachine.2026-07-20-110340.local`.
let private parseTimestamp (name: string) : DateTime option =
    let parts = name.Split '.'
    parts
    |> Array.tryFind (fun p -> p.Length = 17 && p.[4] = '-')
    |> Option.bind (fun stamp ->
        match DateTime.TryParseExact(
                  stamp, "yyyy-MM-dd-HHmmss",
                  Globalization.CultureInfo.InvariantCulture,
                  Globalization.DateTimeStyles.AssumeLocal) with
        | true, value -> Some (value.ToUniversalTime())
        | _ -> None)

/// Lists local snapshots on the boot volume.
/// Returns an empty list on non-macOS platforms rather than an error.
let list () : Result<LocalSnapshot list, string> =
    if Platform.current () <> Types.MacOS then Ok []
    else
        try
            let startInfo =
                ProcessStartInfo(
                    FileName = "tmutil",
                    Arguments = "listlocalsnapshots /",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false)
            use process' = Process.Start startInfo
            let output = process'.StandardOutput.ReadToEnd()
            process'.WaitForExit()
            if process'.ExitCode <> 0 then Ok []      // not an error worth failing a scan over
            else
                output.Split '\n'
                |> Array.map (fun line -> line.Trim())
                |> Array.filter (fun line -> line.StartsWith "com.apple.TimeMachine")
                |> Array.map (fun name -> { Name = name; CreatedUtc = parseTimestamp name })
                |> Array.toList
                |> Ok
        with ex -> Error (sprintf "cannot list local snapshots: %s" ex.Message)

/// Renders advice about local snapshots, or an empty string when there are none.
let renderAdvice (snapshots: LocalSnapshot list) : string =
    if List.isEmpty snapshots then ""
    else
        let oldest =
            snapshots
            |> List.choose (fun s -> s.CreatedUtc)
            |> function [] -> None | dates -> Some (List.min dates)
        let age =
            match oldest with
            | Some date -> sprintf ", oldest from %s" (date.ToLocalTime().ToString "yyyy-MM-dd")
            | None -> ""
        String.concat "\n"
            [ sprintf "  %d Time Machine local snapshot(s) present%s." snapshots.Length age
              "  These can consume more space than everything listed above, and"
              "  deleting files does not reclaim them. To inspect and remove:"
              ""
              "      tmutil listlocalsnapshots /"
              "      tmutil deletelocalsnapshots <date>"
              ""
              "  This tool does not run those commands for you." ]

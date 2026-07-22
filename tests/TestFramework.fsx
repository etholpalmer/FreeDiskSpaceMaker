(**
    Minimal test harness for DiskCleanupAdvisor.

    No third-party test runner is available in a script-only project, so this
    module provides just enough structure: named tests, a handful of assertions,
    and a process exit code so CI can gate on it.
*)
module TestFramework

open System

/// A single recorded test outcome.
type private Outcome = { Suite: string; Name: string; Error: string option }

let mutable private currentSuite = "(root)"
let private outcomes = ResizeArray<Outcome>()

/// Raised by assertions to signal a test failure without aborting the run.
exception AssertionFailure of string

/// Begins a named group of tests. Subsequent `test` calls are attributed to it.
let suite (name: string) =
    currentSuite <- name
    printfn "\n\027[1m%s\027[0m" name

/// Runs a single named test, capturing any assertion failure or exception.
let test (name: string) (body: unit -> unit) =
    try
        body ()
        outcomes.Add { Suite = currentSuite; Name = name; Error = None }
        printfn "  \027[32m✓\027[0m %s" name
    with
    | AssertionFailure msg ->
        outcomes.Add { Suite = currentSuite; Name = name; Error = Some msg }
        printfn "  \027[31m✗\027[0m %s\n      %s" name msg
    | ex ->
        let msg = sprintf "unexpected %s: %s" (ex.GetType().Name) ex.Message
        outcomes.Add { Suite = currentSuite; Name = name; Error = Some msg }
        printfn "  \027[31m✗\027[0m %s\n      %s" name msg

/// Asserts structural equality, reporting both values on mismatch.
let assertEqual (expected: 'a) (actual: 'a) (message: string) =
    if expected <> actual then
        raise (AssertionFailure(sprintf "%s\n      expected: %A\n      actual:   %A" message expected actual))

/// Asserts a condition holds.
let assertTrue (condition: bool) (message: string) =
    if not condition then raise (AssertionFailure message)

/// Asserts a condition does not hold.
let assertFalse (condition: bool) (message: string) =
    if condition then raise (AssertionFailure message)

/// Asserts that `haystack` contains `needle`.
let assertContains (needle: string) (haystack: string) (message: string) =
    if not (haystack.Contains needle) then
        raise (AssertionFailure(sprintf "%s\n      expected to contain: %s\n      actual: %s" message needle haystack))

/// Asserts a Result is Ok and returns its value.
let assertOk (result: Result<'a, 'e>) (message: string) : 'a =
    match result with
    | Ok v -> v
    | Error e -> raise (AssertionFailure(sprintf "%s\n      expected Ok, got Error: %A" message e))

/// Asserts a Result is Error and returns its error.
let assertError (result: Result<'a, 'e>) (message: string) : 'e =
    match result with
    | Error e -> e
    | Ok v -> raise (AssertionFailure(sprintf "%s\n      expected Error, got Ok: %A" message v))

/// Prints a summary and returns a process exit code (0 = all passed).
let report () : int =
    let failed = outcomes |> Seq.filter (fun o -> o.Error.IsSome) |> Seq.toList
    printfn "\n%s" (String.replicate 60 "─")
    if List.isEmpty failed then
        printfn "\027[32m%d passed\027[0m" outcomes.Count
        0
    else
        printfn "\027[31m%d failed\027[0m, %d passed" failed.Length (outcomes.Count - failed.Length)
        for f in failed do printfn "  \027[31m✗\027[0m %s / %s" f.Suite f.Name
        1

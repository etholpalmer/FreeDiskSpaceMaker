(**
    File hashing.

    Two modes: a full streaming hash, and a sampled hash that reads only the
    first and last 64 KB plus the file length. The sampled form is what makes
    duplicate detection affordable — it costs two reads regardless of whether
    the file is 2 MB or 2 GB.

    A sampled hash can collide for genuinely different files, so it is only
    ever used to narrow candidates. A full hash always confirms.
*)
module Hashing

open System
open System.IO
open System.Security.Cryptography

/// Supported digest algorithms.
type Algorithm =
    | Sha256
    | Md5

/// Parses an algorithm name from configuration or the command line.
let parse (text: string) : Result<Algorithm, string> =
    match text.Trim().ToLowerInvariant() with
    | "sha256" -> Ok Sha256
    | "md5" -> Ok Md5
    | other -> Error (sprintf "unsupported hash algorithm '%s' (expected sha256 or md5)" other)

/// Creates a fresh hash implementation. The caller disposes it.
let private create (algorithm: Algorithm) : HashAlgorithm =
    match algorithm with
    | Sha256 -> SHA256.Create() :> HashAlgorithm
    | Md5 -> MD5.Create() :> HashAlgorithm

/// Renders a digest as lowercase hex.
let private toHex (bytes: byte[]) : string =
    Convert.ToHexString(bytes).ToLowerInvariant()

/// How many bytes are read from each end for a sampled hash.
let private sampleWindow = 65_536

/// Streams a file through the chosen algorithm and returns a hex digest.
let full (fs: Platform.FileSystemOps) (algorithm: Algorithm) (path: string) : Result<string, Types.FsError> =
    match fs.OpenRead path with
    | Error e -> Error e
    | Ok stream ->
        use stream = stream
        use hasher = create algorithm
        try Ok (toHex (hasher.ComputeHash stream))
        with ex -> Error (Types.IoFailure(path, ex.Message))

/// Hashes the first and last 64 KB plus the file length.
///
/// Cheap enough to run across every same-size candidate. Files smaller than
/// two windows are hashed in full, since sampling would read them anyway.
let sampled (fs: Platform.FileSystemOps) (path: string) (sizeBytes: int64) : Result<string, Types.FsError> =
    match fs.OpenRead path with
    | Error e -> Error e
    | Ok stream ->
        use stream = stream
        use hasher = SHA256.Create()
        try
            if sizeBytes <= int64 sampleWindow * 2L then
                Ok (toHex (hasher.ComputeHash stream))
            else
                let head = Array.zeroCreate sampleWindow
                let tail = Array.zeroCreate sampleWindow
                stream.ReadExactly(head, 0, sampleWindow)
                stream.Seek(-int64 sampleWindow, SeekOrigin.End) |> ignore
                stream.ReadExactly(tail, 0, sampleWindow)
                // Length participates in the digest so two files sharing both
                // windows but differing in size cannot collide.
                let lengthBytes = BitConverter.GetBytes sizeBytes
                let combined = Array.concat [ head; tail; lengthBytes ]
                Ok (toHex (hasher.ComputeHash combined))
        with ex -> Error (Types.IoFailure(path, ex.Message))

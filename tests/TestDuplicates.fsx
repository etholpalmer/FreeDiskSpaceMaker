(**
    Tests for the file hashing module and the three-stage duplicate funnel.
*)
open System
open TestFramework
open Types
open Platform

let private entry path size days =
    { Path = path; SizeBytes = size; IsDirectory = false; LinkTarget = None; ModifiedDaysAgo = days; ApparentBytes = None }

let private metaOf (fs: FileSystemOps) path =
    match fs.GetInfo path with
    | Ok m -> m
    | Error e -> failwithf "test tree is wrong: %A" e

let run () =
    suite "Duplicates"

    // The in-memory filesystem returns zero-filled content sized to the entry,
    // so files of equal size hash identically and files of unequal size do not.
    let tree =
        [ entry "/a.bin" 4096L 10.0
          entry "/b.bin" 4096L 5.0
          entry "/c.bin" 4096L 20.0
          entry "/d.bin" 8192L 1.0
          entry "/small1.txt" 10L 1.0
          entry "/small2.txt" 10L 1.0 ]
    let fs = FileSystem.inMemory tree
    let metas = tree |> List.map (fun e -> metaOf fs e.Path)

    test "files of identical size and content group together" (fun () ->
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertEqual 1 groups.Length "one duplicate group"
        assertEqual 3 (List.head groups).Paths.Length "three identical 4096-byte files")

    test "a file with a unique size is never a duplicate" (fun () ->
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertFalse (groups |> List.exists (fun g -> g.Paths |> List.contains "/d.bin")) "unique size excluded")

    test "files below the size floor are ignored" (fun () ->
        // Duplicate 10-byte files are not worth the I/O to confirm.
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertFalse (groups |> List.exists (fun g -> g.Paths |> List.exists (fun p -> p.Contains "small")))
            "small files excluded by the floor")

    test "the oldest path is chosen as the keeper" (fun () ->
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertEqual "/c.bin" (List.head groups).Keeper "the 20-day-old file is kept")

    test "wasted bytes count every copy except the keeper" (fun () ->
        let groups = Duplicates.find fs Hashing.Sha256 1024L metas
        assertEqual 8192L (List.head groups).Wasted "two redundant copies of 4096 bytes")

    test "an empty input yields no groups" (fun () ->
        assertEqual 0 (Duplicates.find fs Hashing.Sha256 1024L []).Length "no input, no groups")

    suite "Hashing"

    test "algorithm names parse case-insensitively" (fun () ->
        assertEqual Hashing.Sha256 (assertOk (Hashing.parse "SHA256") "sha256") "sha256"
        assertEqual Hashing.Md5 (assertOk (Hashing.parse "md5") "md5") "md5"
        assertError (Hashing.parse "crc32") "unsupported algorithm" |> ignore)

    test "hashing the same content twice yields the same digest" (fun () ->
        let h1 = assertOk (Hashing.full fs Hashing.Sha256 "/a.bin") "first hash"
        let h2 = assertOk (Hashing.full fs Hashing.Sha256 "/b.bin") "second hash"
        assertEqual h1 h2 "identical content hashes identically")

    test "hashing a missing file returns an error rather than throwing" (fun () ->
        assertError (Hashing.full fs Hashing.Sha256 "/nope") "missing file" |> ignore)

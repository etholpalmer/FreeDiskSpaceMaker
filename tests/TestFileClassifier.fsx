(**
    Tests for FileClassifier — verifies directory context beats extension,
    compound-extension handling, and case-insensitive matching.
*)
open TestFramework
open Types
open FileClassifier

let run () =
    suite "FileClassifier"

    test "archives are recognized by extension" (fun () ->
        for name in [ "a.zip"; "a.tar"; "a.tar.gz"; "a.tgz"; "a.rar"; "a.7z"; "a.bz2" ] do
            assertEqual Archives (classify ("/x/" + name)) (sprintf "%s is an archive" name))

    test "disk images are recognized by extension" (fun () ->
        for name in [ "a.dmg"; "a.iso"; "a.img"; "a.vmdk"; "a.qcow2" ] do
            assertEqual DiskImages (classify ("/x/" + name)) (sprintf "%s is a disk image" name))

    test "installers are recognized by extension" (fun () ->
        for name in [ "a.pkg"; "a.msi"; "a.deb"; "a.rpm" ] do
            assertEqual Installers (classify ("/x/" + name)) (sprintf "%s is an installer" name))

    test "logs are recognized by extension and by rotation suffix" (fun () ->
        assertEqual Logs (classify "/x/app.log") "plain log"
        assertEqual Logs (classify "/x/app.log.1") "rotated log"
        assertEqual Logs (classify "/x/app.log.gz") "compressed rotated log")

    test "crash reports are recognized" (fun () ->
        assertEqual CrashReports (classify "/x/App_2024-01-01.crash") "crash extension"
        assertEqual CrashReports (classify "/x/App.ips") "ips extension")

    test "temporary files are recognized" (fun () ->
        assertEqual TemporaryFiles (classify "/x/a.tmp") "tmp extension"
        assertEqual TemporaryFiles (classify "/x/a.temp") "temp extension"
        assertEqual TemporaryFiles (classify "/x/~a.swp") "swap file")

    test "path context overrides extension when a directory is decisive" (fun () ->
        // A .log inside a cache directory is still cache, because the
        // enclosing directory is the stronger signal about regenerability.
        assertEqual Caches (classify "/Users/x/Library/Caches/app/debug.log") "cache directory wins"
        assertEqual DeveloperArtifacts (classify "/Users/x/Library/Developer/Xcode/DerivedData/a/b.o") "DerivedData wins")

    test "node_modules and __pycache__ are developer artifacts" (fun () ->
        assertEqual DeveloperArtifacts (classify "/p/node_modules/left-pad/index.js") "node_modules"
        assertEqual DeveloperArtifacts (classify "/p/__pycache__/mod.cpython-312.pyc") "__pycache__")

    test "an unrecognized file falls through to Other" (fun () ->
        assertEqual Other (classify "/x/notes.xyz") "unknown extension")

    test "classification is case-insensitive" (fun () ->
        assertEqual Archives (classify "/x/BACKUP.ZIP") "uppercase extension")

    test "isArchive and isDiskImage agree with classify" (fun () ->
        assertTrue (isArchive "/x/a.zip") "zip is an archive"
        assertFalse (isArchive "/x/a.dmg") "dmg is not an archive"
        assertTrue (isDiskImage "/x/a.dmg") "dmg is a disk image")

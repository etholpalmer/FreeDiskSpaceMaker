(**
    Test entry point, and one of only two files in the project permitted to
    contain #load directives. Lists every file exactly once in topological
    order — see the Canonical load order in the plan header.

    Run with:  dotnet fsi tests/Tests.fsx
*)
#load "../src/Types.fsx"
#load "../src/Utilities.fsx"
#load "../src/Logging.fsx"
#load "../src/Diagnostics.fsx"
#load "../src/Platform.fsx"
#load "../src/Configuration.fsx"
#load "../src/Rules.fsx"
#load "../src/RiskEngine.fsx"
#load "../src/FileClassifier.fsx"
#load "../src/DirectoryWalker.fsx"
#load "../src/Hashing.fsx"
#load "../src/Duplicates.fsx"
#load "../src/Scanner.fsx"
#load "../src/Report/Dto.fsx"
#load "../src/Report/Json.fsx"
#load "../src/Snapshots.fsx"
#load "../src/Report/Console.fsx"
#load "../src/Report/Markdown.fsx"
#load "../src/Report/Html.fsx"
#load "../src/Cli.fsx"
#load "../src/Cleaner.fsx"
#load "TestFramework.fsx"
#load "TestTypes.fsx"
#load "TestUtilities.fsx"
#load "TestDiagnostics.fsx"
#load "TestPlatform.fsx"
#load "TestConfiguration.fsx"
#load "TestRules.fsx"
#load "TestRiskEngine.fsx"
#load "TestFileClassifier.fsx"
#load "TestDirectoryWalker.fsx"
#load "TestScanner.fsx"
#load "TestDuplicates.fsx"
#load "TestDto.fsx"
#load "TestCli.fsx"
#load "TestCleaner.fsx"
#load "TestReports.fsx"

TestTypes.run ()
TestUtilities.run ()
TestDiagnostics.run ()
TestPlatform.run ()
TestConfiguration.run ()
TestRules.run ()
TestRiskEngine.run ()
TestFileClassifier.run ()
TestDirectoryWalker.run ()
TestScanner.run ()
TestDuplicates.run ()
TestDto.run ()
TestCli.run ()
TestCleaner.run ()
TestReports.run ()

exit (TestFramework.report ())

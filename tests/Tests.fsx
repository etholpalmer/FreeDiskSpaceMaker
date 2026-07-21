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
#load "../src/Report/Dto.fsx"
#load "TestFramework.fsx"
#load "TestTypes.fsx"
#load "TestUtilities.fsx"
#load "TestDiagnostics.fsx"
#load "TestPlatform.fsx"
#load "TestConfiguration.fsx"
#load "TestRules.fsx"
#load "TestRiskEngine.fsx"
#load "TestDto.fsx"

TestTypes.run ()
TestUtilities.run ()
TestDiagnostics.run ()
TestPlatform.run ()
TestConfiguration.run ()
TestRules.run ()
TestRiskEngine.run ()
TestDto.run ()

exit (TestFramework.report ())

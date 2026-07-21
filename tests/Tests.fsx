(**
    Test entry point, and one of only two files in the project permitted to
    contain #load directives. Lists every file exactly once in topological
    order — see the Canonical load order in the plan header.

    Run with:  dotnet fsi tests/Tests.fsx
*)
#load "../src/Types.fsx"
#load "TestFramework.fsx"
#load "TestTypes.fsx"

TestTypes.run ()

exit (TestFramework.report ())

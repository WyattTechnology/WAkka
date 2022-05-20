open Wtc.FakeUtilities
    
let targets = WtcNugetPackage.makeWtcNugetPackageTargets {
    project = "WAkka\WAkka\WAkka.fsproj"
    testProject = "WAkka\WAkkaTests\WAkkaTests.fsproj"
    testExtraModuleExcludes = ["WAkkaTests"; "AltCover"]
    testFileExcludes = []
    testMinCoveragePercent = 76.0
}

Run.runBuild ()

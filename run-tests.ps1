<#
.Synopsis
Runs the unit tests and checks for minimum code coverage.

.Parameter Config
The build config to test.

#>

[cmdletbinding(SupportsShouldProcess)]
param(
    [validateset('Release', 'Debug')][string]$Config = "Debug"
)


$ProjectDir="./WAkka/WAkkaTests"
$MinCoverage = 77.0
$ExcludeAssemblies = "WAkkaTests|AltCover"
$ExcludeFiles = ""

try {
    dotnet tool restore
    cd $ProjectDir
    dotnet test -c $Config /p:AltCover=true /p:AltCoverForce=true /p:AltCoverAssemblyFilter="NUnit|xunit|FSharp|WAkkaTests|AltCover"
    
    reportgenerator -reports:coverage.xml -targetdir:coverageA

    reportgenerator -reports:coverage.xml -targetdir:. -reporttypes:XmlSummary

    if($PSCmdlet.ShouldProcess($ProjectDir, "Checking coverage")) {
        [xml]$report = Get-Content "Summary.xml"
        [float]$cov = $report.CoverageReport.Summary.Linecoverage
        if ($cov -lt $MinCoverage){
            $err = "Coverage for $Config $ProjectDir too low ($cov% < $MinCoverage%)"
            Write-Host -ForegroundColor Red $err
            exit 1
        } else{
            Write-Host -ForegroundColor Green "Test coverage good: $cov% >= $MinCoverage%"
        }
    }
}
finally {
    cd ../..
}

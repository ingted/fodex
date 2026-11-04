# Run the existing M017 script in this console. Build the implementation in Debug first.
# Edit defaultArgumentsText in WrapperIAgentDemo.M017.fsx; use --interactive true here.
# This launcher owns no actor, process supervisor, argument parser, or execution state.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$demoScript = Join-Path $PSScriptRoot 'WrapperIAgentDemo.M017.fsx'
$implementationDll = Join-Path $PSScriptRoot 'bin/Debug/net10.0/win-x64/Fodex.Supervisor.dll'
if (-not (Test-Path -LiteralPath $implementationDll -PathType Leaf)) {
    throw 'Build Fodex.Supervisor.fsproj in Debug before running Start-M017.ps1.'
}

Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet fsi --debug- --exec $demoScript
    if ($LASTEXITCODE -ne 0) {
        throw "M017 exited with code $LASTEXITCODE. See the error above."
    }
}
finally {
    Pop-Location
}

# Run the existing M018 script in this console. Build the implementation in Debug first.
# Edit defaultArgumentsText in WrapperIAgentDemo.M018.fsx; optional CLI arguments override those defaults.
# This launcher owns no actor, process supervisor, argument parser, or execution state.
# Keep this a simple script so arguments such as --session remain untouched in $args.
param()

$ErrorActionPreference = 'Stop'
$demoScript = Join-Path $PSScriptRoot 'WrapperIAgentDemo.M018.fsx'
$implementationDll = Join-Path $PSScriptRoot 'bin/Debug/net10.0/win-x64/Fodex.Supervisor.dll'
if (-not (Test-Path -LiteralPath $implementationDll -PathType Leaf)) {
    throw 'Build Fodex.Supervisor.fsproj in Debug before running Start-M018.ps1.'
}

Push-Location -LiteralPath $PSScriptRoot
try {
    # Temporary mitigation for FSI reference-import lock waits observed with SDK 11 RC2.
    # Serialize script compilation only; Fodex actors keep their normal concurrency.
    # This compiler test option intentionally leaves warning FS0075 visible.
    & dotnet fsi --parallelcompilation- --debug- --exec $demoScript -- @args
    if ($LASTEXITCODE -ne 0) {
        throw "M018 exited with code $LASTEXITCODE. See the error above."
    }
}
finally {
    Pop-Location
}

# Interactive alias for the existing compiled supervisor executable; no FSI or test runner.
$appName = if ($env:OS -eq 'Windows_NT') { 'Fodex.Supervisor.exe' } else { 'Fodex.Supervisor' }
$cliPath = Join-Path $PSScriptRoot (Join-Path 'bin/CLI' $appName)
if (-not (Test-Path -LiteralPath $cliPath)) {
    throw 'Build Fodex.Supervisor.fsproj with -c Release -p:OutDir=bin/CLI/ first. See README.M022.md.'
}
& $cliPath @args
exit $LASTEXITCODE

# Automation convenience; normal interactive review uses the script's defaultArgumentsText.
$m021Script = Join-Path $PSScriptRoot 'WrapperIAgentDemo.M021.fsx'
& dotnet fsi --debug- --parallelcompilation- --exec $m021Script -- @args
exit $LASTEXITCODE

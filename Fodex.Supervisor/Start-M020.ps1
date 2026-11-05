# Run the M020 script in this console. Build the implementation in Debug first.
# Default adapter: agy (Google Antigravity CLI); subagent-type: codex.
# Edit defaultArgumentsText in WrapperIAgentDemo.M020.fsx; optional CLI arguments override those defaults.
# Usage examples:
#   .\Start-M020.ps1                                              # Interactive mode (預設 main=agy, subagent=codex)
#   .\Start-M020.ps1 --mainagent-type codex                       # 指定 Main Agent 為 Codex (app-server)
#   .\Start-M020.ps1 --mainagent-type codex --native-adapter exec # 指定 Main Agent 為 Codex CLI exec
#   .\Start-M020.ps1 --subagent-type agy                          # 指定預設 Subagent 為 agy
#   .\Start-M020.ps1 --interactive false --prompt "Hello"         # Headless single prompt execution
param()

$ErrorActionPreference = 'Stop'
$demoScript = Join-Path $PSScriptRoot 'WrapperIAgentDemo.M020.fsx'
$implementationDll = Join-Path $PSScriptRoot 'bin/Debug/net10.0/win-x64/Fodex.Supervisor.dll'
if (-not (Test-Path -LiteralPath $implementationDll -PathType Leaf)) {
    throw 'Build Fodex.Supervisor.fsproj in Debug before running Start-M020.ps1.'
}

Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet fsi --parallelcompilation- --debug- --exec $demoScript -- @args
    if ($LASTEXITCODE -ne 0) {
        throw "M020 exited with code $LASTEXITCODE. See the error above."
    }
}
finally {
    Pop-Location
}

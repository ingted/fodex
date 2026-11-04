param(
    [string]$Source = 'F:\fodex\Fodex.Supervisor.Tests\M019LineV2Acceptance.fsx',
    [string]$OutputRoot = 'F:\fodex\temp\sub-5\M019-line-v2-runner',
    [string]$SqlServer = 'localhost',
    [int]$TimeoutSeconds = 900,
    [switch]$RecoveryOnly
)
# Own-process watchdog only. The real v2 entry is executed directly by the Expecto controller.
$ErrorActionPreference='Stop'
if([IO.Path]::GetFullPath($Source)-ne'F:\fodex\Fodex.Supervisor.Tests\M019LineV2Acceptance.fsx'){throw 'Unexpected watchdog test source'}
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$stdout=Join-Path $OutputRoot 'acceptance.stdout.log'
$stderr=Join-Path $OutputRoot 'acceptance.stderr.log'
$testArguments=@('fsi','--parallelcompilation-','--debug-','--define:V2_PINNED_8C9B80E','--exec',$Source,'--','--sql-server',$SqlServer)
if($RecoveryOnly){$testArguments+=@('--recovery-only','true')}
$testProcess=Start-Process -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList $testArguments -WorkingDirectory F:\fodex -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
$timedOut=-not $testProcess.WaitForExit($TimeoutSeconds*1000)
if($timedOut){$testProcess.Kill($true);$testProcess.WaitForExit(15000)|Out-Null}
$exitCode=if($timedOut){124}else{$testProcess.ExitCode}
# Recovery is limited to a DB name emitted by this owned test and validated by exact prefix/GUID.
$reader=[IO.StreamReader]::new($stdout,[Text.UTF8Encoding]::new($false,$false))
try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
$match=[regex]::Match($text,'M019 database-created (M019LineV2Test_[a-f0-9]{32})')
if($match.Success){
    $testDatabase=$match.Groups[1].Value
    & sqlcmd.exe -S $SqlServer -E -d master -b -Q "IF DB_ID(N'$testDatabase') IS NOT NULL BEGIN ALTER DATABASE [$testDatabase] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$testDatabase]; END"
    if($LASTEXITCODE-ne0){$exitCode=1}
    $remaining=& sqlcmd.exe -S $SqlServer -E -d master -b -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name=N'$testDatabase';"
    [IO.File]::WriteAllText((Join-Path $OutputRoot 'cleanup.txt'),('database='+$testDatabase+' remaining='+($remaining-join' ').Trim()),[Text.UTF8Encoding]::new($false))
}
[IO.File]::WriteAllText((Join-Path $OutputRoot 'exit.txt'),[string]$exitCode,[Text.UTF8Encoding]::new($false))
exit $exitCode

param([int]$TimeoutSeconds=180)
$ErrorActionPreference='Stop'
$fixtureId=[Guid]::NewGuid().ToString('N')
$fixtureRoot='F:\fodex\temp\sub-5\M019-line-v2-adapter\'+$fixtureId
$database='PTCLineTest_'+$fixtureId
$fsiRoot=$fixtureRoot.Replace('\','/')
[IO.Directory]::CreateDirectory($fixtureRoot)|Out-Null
$dummy=$fsiRoot+'/fixture-reference-only.txt'
[IO.File]::WriteAllText(($fixtureRoot+'\fixture-reference-only.txt'),'synthetic fixture, no credentials or authority',[Text.UTF8Encoding]::new($false))
$catalog=@{Account='sub5-isolated-account';SourceParticipantId='sub5.synthetic';RecipientParticipantId='sub5.adapter';Generation=1;PrivateKeyPath=$dummy;GatewayPublicKeyPath=$dummy;ChannelSecretEncryptedFile=$dummy;DestinationEncryptedFile=$dummy;AccessTokenEncryptedFile=$dummy;SqlConnectionEncryptedFile=$dummy;PtcsIngressUrl='http://127.0.0.1:1/unused';MediaPublicOrigin='https://sub5.invalid'}
[IO.File]::WriteAllText(($fixtureRoot+'\unused-isolated-catalog.json'),($catalog|ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText('F:\fodex\temp\sub-5\M019-line-v2-adapter-latest.txt',$fixtureRoot,[Text.UTF8Encoding]::new($false))
$code=1
$child=$null
try {
    & sqlcmd.exe -S localhost -E -d master -b -Q "CREATE DATABASE [$database]"
    if($LASTEXITCODE-ne0){throw 'Owned adapter DB create failed'}
    foreach($mode in @('warm','cold')) {
        $arguments=@('fsi','--parallelcompilation-','--debug-','--define:V2_PINNED_8C9B80E','--exec','F:\fodex\Fodex.Supervisor.Tests\M019LineV2AdapterFixture.fsx','--','--mode','verify','--prompt',$mode,'--sql-server','localhost','--database',$database,'--output-root',$fsiRoot,'--media-root',($fsiRoot+'/media'),'--working-directory',$fsiRoot,'--line-catalog',($fsiRoot+'/unused-isolated-catalog.json'))
        $child=Start-Process 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput ($fixtureRoot+'\'+$mode+'.stdout.log') -RedirectStandardError ($fixtureRoot+'\'+$mode+'.stderr.log')
        [IO.File]::AppendAllText(($fixtureRoot+'\processes.txt'),("$mode pid="+$child.Id+"`n"),[Text.UTF8Encoding]::new($false))
        if(-not $child.WaitForExit($TimeoutSeconds*1000)){$child.Kill($true);$child.WaitForExit(15000)|Out-Null;throw 'Owned adapter watchdog expired'}
        [IO.File]::AppendAllText(($fixtureRoot+'\processes.txt'),("$mode exit="+$child.ExitCode+"`n"),[Text.UTF8Encoding]::new($false))
        if($child.ExitCode-ne0){throw "Adapter $mode failed"}
    }
    $code=0
} catch {
    [IO.File]::WriteAllText(($fixtureRoot+'\failure.txt'),$_.Exception.Message,[Text.UTF8Encoding]::new($false))
} finally {
    if($child-and -not $child.HasExited){$child.Kill($true);$child.WaitForExit(15000)|Out-Null}
    & sqlcmd.exe -S localhost -E -d master -b -Q "IF DB_ID(N'$database') IS NOT NULL BEGIN ALTER DATABASE [$database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$database]; END"
    $readback=& sqlcmd.exe -S localhost -E -d master -b -W -h -1 -Q "SET NOCOUNT ON;SELECT COUNT(*) FROM sys.databases WHERE name=N'$database'; SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE database_id=DB_ID(N'$database') AND session_id<>@@SPID;"
    [IO.File]::WriteAllText(($fixtureRoot+'\cleanup.txt'),("database=$database readback="+($readback-join' ').Trim()),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(($fixtureRoot+'\exit.txt'),[string]$code,[Text.UTF8Encoding]::new($false))
}
exit $code

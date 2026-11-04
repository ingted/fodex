param([string]$Root='F:\fodex\temp\sub-5\M019-v2-R6-20261104184359')
$ErrorActionPreference='Stop'
if([IO.Path]::GetFullPath($Root)-notmatch'^F:\\fodex\\temp\\sub-5\\M019-v2-R6-[0-9]{14}$'){throw 'Owned R6 root required'}
$fsi='C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\FSharp\fsi.dll'
$target='G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.v2.fsx'
$database='M019R6Native_'+[Guid]::NewGuid().ToString('N')
$conversation='sub5-r6-'+[Guid]::NewGuid().ToString('N')
$processes=[Collections.Generic.List[object]]::new()
$results=[Collections.Generic.List[object]]::new()
$active=[Collections.Generic.List[Diagnostics.Process]]::new()
$u=[Text.UTF8Encoding]::new($false,$true)
function Sql([string]$Db,[string]$Query){
    $text=& sqlcmd.exe -S localhost -E -d $Db -b -h -1 -W -t 15 -Q ('SET NOCOUNT ON;'+$Query) 2>&1
    if($LASTEXITCODE-ne0){throw ('Owned SQL failed: '+($text-join' '))}
    return ($text-join"`n").Trim()
}
function Check([bool]$condition,[string]$why){if(-not$condition){throw $why}}
function Run([string]$Name,[string]$Source,[string[]]$Tail){
    $psi=[Diagnostics.ProcessStartInfo]::new('C:\Program Files\dotnet\dotnet.exe')
    $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WorkingDirectory=$Root
    $psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
    $psi.Environment['M019_R6_ROOT']=$Root
    foreach($arg in @($fsi,'--parallelcompilation-','--debug-','--define:V2_PINNED_8C9B80E','--exec',$Source)+$Tail){$psi.ArgumentList.Add($arg)}
    $p=[Diagnostics.Process]::Start($psi);$active.Add($p)
    $out=[IO.File]::Open(($Root+'\'+$Name+'.stdout.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $err=[IO.File]::Open(($Root+'\'+$Name+'.stderr.log'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    $copyOut=$p.StandardOutput.BaseStream.CopyToAsync($out);$copyErr=$p.StandardError.BaseStream.CopyToAsync($err)
    $started=Get-Date -Format o
    try {
        if(-not$p.WaitForExit(120000)){$p.Kill($true);$p.WaitForExit(15000)|Out-Null;throw ('Owned watchdog expired '+$Name)}
        [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($copyOut,$copyErr)).GetAwaiter().GetResult()
        $row=[pscustomobject]@{name=$Name;pid=$p.Id;exit=$p.ExitCode;started=$started;ended=(Get-Date -Format o);source=$Source;args=[string[]]@($psi.ArgumentList)}
        $processes.Add($row)
        return $row
    } finally {
        if(-not$p.HasExited){$p.Kill($true);$p.WaitForExit(15000)|Out-Null}
        $out.Dispose();$err.Dispose()
    }
}
function NativeArgs([Guid]$Route,[string]$Prompt){
    @('--','--mode','request','--sql-server','localhost','--database',$database,'--conversation',$conversation,'--target','main','--route',$Route.ToString(),'--model','gpt-6.1-sol','--effort','low','--working-directory',$Root.Replace('\','/'),'--output-root',($Root.Replace('\','/')+'/native'),'--prompt',$Prompt)
}
try {
    Sql master ('CREATE DATABASE ['+$database+']')|Out-Null
    [IO.File]::WriteAllText(($Root+'\native-database.txt'),$database,$u)
    try {
        $nonce='SUB5_R6_'+[Guid]::NewGuid().ToString('N')
        $first=Run native-start $target (NativeArgs ([Guid]::NewGuid()) ('Remember this token '+$nonce+'. Reply exactly SUB5_R6_FIRST_OK in English.'))
        Check ($first.exit-eq0) 'Formal first native exit failed'
        Check ([IO.File]::ReadAllText(($Root+'\native-start.stdout.log'),$u).Contains('SUB5_R6_FIRST_OK')) 'First native oracle failed'
        $query="SELECT CONVERT(varchar(36),WrapperId)+'|'+COALESCE(NativeSid,'')+'|'+CONVERT(varchar(20),Generation) FROM dbo.LineFodexV2Sessions WHERE Conversation=N'$conversation' AND Target='main';"
        $identity=Sql $database $query
        $parts=$identity.Split('|');Check ($parts.Length-eq3 -and $parts[1].Length-gt0) 'Persisted native SID missing'
        $prompt='Use the available view_image tool on '+($Root.Replace('\','/')+'/owned-delta.png')+'. Reply in English only with the token you remember, then the four quadrant colors in order top-left, top-right, bottom-left, bottom-right. Inspect the actual image. Do not guess.'
        $second=Run native-resume $target (NativeArgs ([Guid]::NewGuid()) $prompt)
        Check ($second.exit-eq0 -and $second.pid-ne$first.pid) 'Different provider process did not complete'
        Check ((Sql $database $query)-eq$identity) 'Wrapper/native SID/generation changed'
        $reply=[IO.File]::ReadAllText(($Root+'\native-resume.stdout.log'),$u)
        Check ($reply.Contains($nonce)) 'Native memory not resumed'
        Check ([regex]::IsMatch($reply,'(?is)blue.*yellow.*red.*green')) 'Independent image semantic order failed'
        $nativeSid=$parts[1]
        $day=Join-Path $HOME ('.codex\sessions\'+[DateTime]::UtcNow.ToString('yyyy/MM/dd',[Globalization.CultureInfo]::InvariantCulture))
        $audits=@(Get-ChildItem -LiteralPath $day -Filter ('*'+$nativeSid+'.jsonl') -File)
        Check ($audits.Count-eq1) 'Exact owned native audit unavailable'
        $imageCalls=@{};$imageOutputs=0;$contexts=[Collections.Generic.List[object]]::new()
        foreach($line in [IO.File]::ReadLines($audits[0].FullName)){
            $event=$line|ConvertFrom-Json
            if($event.type-eq'turn_context'){$contexts.Add([pscustomobject]@{at=$event.timestamp;model=$event.payload.model;sandbox=$event.payload.sandbox_policy})}
            if($event.type-ne'response_item'){continue};$v=$event.payload
            if($v.type-in@('custom_tool_call','function_call') -and (($v.input+$v.arguments)-match'view_image' -or $v.name-eq'view_image') -and ($v.input+$v.arguments)-match'owned-delta.png'){$imageCalls[$v.call_id]=$true}
            if($v.type-in@('custom_tool_call_output','function_call_output') -and $imageCalls.ContainsKey($v.call_id)){$imageOutputs++}
        }
        Check ($imageCalls.Count-ge1 -and $imageOutputs-ge1) 'Actual owned image call/result not observed'
        $results.Add([pscustomobject]@{case='formal-native-process-memory-owned-image';passed=$true;identity=$identity;firstPid=$first.pid;secondPid=$second.pid;imageCalls=$imageCalls.Count;imageOutputs=$imageOutputs;auditPath=$audits[0].FullName;auditSHA=(Get-FileHash $audits[0].FullName).Hash;contexts=$contexts;semanticOracle='BLUE YELLOW RED GREEN';imageSHA=(Get-FileHash ($Root+'\owned-delta.png')).Hash})
    } catch {$results.Add([pscustomobject]@{case='formal-native-process-memory-owned-image';passed=$false;error=$_.Exception.Message})}
    foreach($suite in @(@('protocol','M019LineV2R6ToolProtocol.fsx'),@('sql','M019LineV2R6Sql.fsx'))){
        try{$r=Run $suite[0] ('F:\fodex\Fodex.Supervisor.Tests\'+$suite[1]) @();Check ($r.exit-eq0) ($suite[0]+' suite failed');$results.Add([pscustomobject]@{case=$suite[0];passed=$true;pid=$r.pid})}
        catch{$results.Add([pscustomobject]@{case=$suite[0];passed=$false;error=$_.Exception.Message})}
    }
} finally {
    foreach($p in $active){if(-not$p.HasExited){$p.Kill($true);$p.WaitForExit(15000)|Out-Null}}
    $dbs=@($database)
    if(Test-Path ($Root+'\sql-database.txt')){$sqlDb=[IO.File]::ReadAllText($Root+'\sql-database.txt');if($sqlDb-match'^M019R6Sql_[a-f0-9]{32}$'){$dbs+=$sqlDb}}
    foreach($db in $dbs){Sql master ("IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;DROP DATABASE [$db];END")|Out-Null}
    $remaining=Sql master ("SELECT COUNT(*) FROM sys.databases WHERE name IN ("+(($dbs|ForEach-Object{"N'$_'"})-join',')+");")
    [pscustomobject]@{at=(Get-Date -Format o);results=$results;processes=$processes;ownedDatabases=$dbs;remainingDatabases=$remaining;createdLogins=0}|ConvertTo-Json -Depth 12|Set-Content ($Root+'\delta-results.json') -Encoding UTF8
}
if(@($results|Where-Object{-not$_.passed}).Count-gt0){exit 1}

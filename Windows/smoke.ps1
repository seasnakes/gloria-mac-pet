# Run in a fresh Windows PowerShell process. -Desktop also checks the visible pet windows.
param([Parameter(Mandatory=$true)][string]$Zip, [switch]$Desktop)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$testRoot=Join-Path $env:TEMP ('GLORIA smoke '+[guid]::NewGuid())
$bin=Join-Path $testRoot "GLORIA's pets"
$null=New-Item -Type Directory $testRoot
$env:GLORIA_DATA_DIR=Join-Path $testRoot profile
$env:GLORIA_HOOK_HOME=Join-Path $testRoot home
$env:CLAUDE_CONFIG_DIR=Join-Path $testRoot claude
$env:CODEX_HOME=Join-Path $testRoot codex
$env:DSH_HOME=Join-Path $testRoot dsh
$app=Join-Path $bin 'GLORIA.exe'
function Configure($source,$state,$expected=0) {
    $r=Start-Process -FilePath $app -ArgumentList @('--hooks',$source,$state) -PassThru -Wait
    if($r.ExitCode -ne $expected){throw "$source $state returned $($r.ExitCode), expected $expected"}
}
function Wait-Pose($id,$pose,$timeout=5) {
    $until=[datetime]::UtcNow.AddSeconds($timeout)
    $title="GLORIA $([char]0xb7) $id $([char]0xb7) $pose"
    do {
        $h=[GloriaWindowsTest]::FindWindow([System.Management.Automation.Language.NullString]::Value,$title)
        if($h -ne [IntPtr]::Zero){
            [uint32]$owner=0
            $null=[GloriaWindowsTest]::GetWindowThreadProcessId($h,[ref]$owner)
            if($owner -eq $process.Id -and [GloriaWindowsTest]::IsWindowVisible($h)){Write-Host "$id -> $pose";return}
        }
        Start-Sleep -Milliseconds 80
    } while([datetime]::UtcNow -lt $until)
    $process.Refresh(); throw "Visible window not found: $title; process exited=$($process.HasExited), main window=$($process.MainWindowTitle)"
}
try {
    Expand-Archive -LiteralPath $Zip -DestinationPath $bin
    $cmds=@{}
    foreach($source in @('claude','codex','cursor','dsh')){
        $folder=switch($source){claude{$env:CLAUDE_CONFIG_DIR}codex{$env:CODEX_HOME}cursor{Join-Path $env:GLORIA_HOOK_HOME '.cursor'}dsh{$env:DSH_HOME}}
        $file=if($source -eq 'claude'){Join-Path $folder 'settings.json'}else{Join-Path $folder 'hooks.json'}
        $null=New-Item -Type Directory $folder -Force
        $fixture=if($source -eq 'cursor'){'{"version":1,"hooks":{"stop":[{"command":"keep-me"}]}}'}else{'{"custom":"keep","hooks":{"Stop":[{"matcher":"custom","hooks":[{"type":"command","command":"keep-me"}]}]}}'}
        [IO.File]::WriteAllText($file,$fixture)
        Configure $source on
        $first=[IO.File]::ReadAllText($file)
        Configure $source on
        if([IO.File]::ReadAllText($file) -ne $first){throw "Duplicated hooks: $source"}
        $cfg=$first | ConvertFrom-Json
        $cmds[$source]=if($source -eq 'cursor'){$cfg.hooks.postToolUse[0]}else{$cfg.hooks.PreToolUse[0].hooks[0]}
        Configure $source off
        $restored=([IO.File]::ReadAllText($file) | ConvertFrom-Json | ConvertTo-Json -Depth 12 -Compress)
        if($restored -cne $fixture){throw "Did not preserve settings: $source"}
        if(!(Get-ChildItem "$file.gloria-*.bak")){throw 'Backup missing'}
        [IO.File]::WriteAllText($file,'{broken')
        Configure $source on 1
        if([IO.File]::ReadAllText($file) -ne '{broken'){throw 'Touched invalid JSON'}
    }
    Write-Host 'Configuration installation, repeat installation, removal, backup and invalid JSON checks passed.'
    if($Desktop){
        Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class GloriaWindowsTest {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@
        $process=Start-Process $app -PassThru
        Start-Sleep -Milliseconds 1500
        # Run exactly the handler stored in each generated configuration.
        $cmd=$cmds.claude.command; $cliArgs=$cmds.claude.args
        # Observe a real helper-to-window round trip, including an unrelated completion.
        '{"hook_event_name":"PreToolUse","session_id":"mail","tool_use_id":"job","tool_name":"PowerShell","tool_input":{"command":"& \"C:\\Program Files\\Codex\\codex.cmd\" exec hello"}}' | & $cmd @cliArgs
        Wait-Pose doro jump
        Wait-Pose doro work
        '{"hook_event_name":"PostToolUse","session_id":"other","tool_use_id":"job","tool_name":"PowerShell"}' | & $cmd @cliArgs
        '{"hook_event_name":"PostToolUse","session_id":"mail","tool_use_id":"unrelated","tool_name":"Read"}' | & $cmd @cliArgs
        Start-Sleep -Milliseconds 500
        Wait-Pose doro work
        '{"hook_event_name":"PostToolUseFailure","session_id":"mail","tool_use_id":"job","tool_name":"PowerShell"}' | & $cmd @cliArgs
        Wait-Pose doro failed
        Wait-Pose usagi jump
        '{"hook_event_name":"Stop","session_id":"other"}' | & $cmd @cliArgs
        '{"hook_event_name":"Stop","session_id":"mail"}' | & $cmd @cliArgs
        Wait-Pose usagi wave
        '{"hook_event_name":"PermissionRequest","session_id":"A"}' | & $cmd @cliArgs
        Wait-Pose usagi waiting
        '{"hook_event_name":"Stop","session_id":"B"}' | & $cmd @cliArgs
        Wait-Pose usagi waiting
        '{"hook_event_name":"Stop","session_id":"A"}' | & $cmd @cliArgs
        Wait-Pose usagi wave
        # A maximized target has no top ledge. Exercise the walk before the typing pose.
        $helper=Join-Path $bin 'GLORIA-Hook.exe'
        $deskSnapshot='{"ctl":"desk"}' | & $helper --ctl - | ConvertFrom-Json
        $area=$deskSnapshot.screens[0].working
        $rect=@($area[0],$area[1],[Math]::Min(1200,$area[2]),$area[3])
        @{ctl='stage';wins=@(@{owner='SmokeTarget';rect=$rect})} | ConvertTo-Json -Depth 5 -Compress | & $helper --ctl - | Out-Null
        @{ctl='place';pet='doro';x=($area[0]+80);y=($area[1]+$area[3]-2)} | ConvertTo-Json -Compress | & $helper --ctl - | Out-Null
        Start-Sleep -Milliseconds 650
        # Only execute our just-generated commands in this isolated fixture.
        '{"hook_event_name":"PreToolUse","tool_name":"mcp__cua_repl__js","tool_input":{"code":"x.typeText({})"}}' | & ([scriptblock]::Create('$input | '+$cmds.codex.command))
        Wait-Pose doro type 20
        '{"hook_event_name":"postToolUse","tool_name":"Read"}' | & ([scriptblock]::Create('$input | '+$cmds.cursor.command)) | Out-Null
        Wait-Pose gloria work
        '{"hook_event_name":"PreToolUse","tool_name":"read"}' | & ([scriptblock]::Create('$input | '+$cmds.dsh.command))
        Wait-Pose grokbot work
        $again=Start-Process $app -PassThru
        if(!$again.WaitForExit(4000)){ $again.Kill();throw 'Second instance did not exit' }
        Write-Host 'Four visible pets, hook routing and single instance checks passed.'
    }
} finally {
    if($process -and !$process.HasExited){$process.Kill();$process.WaitForExit()}
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

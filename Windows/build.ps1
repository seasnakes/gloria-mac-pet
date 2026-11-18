# Windows PowerShell 5.1 or PowerShell 7. No SDK/NuGet restore required.
[CmdletBinding()] param([string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$framework = @('FrameworkArm64','Framework64','Framework') | ForEach-Object {
    Join-Path $env:WINDIR "Microsoft.NET\$_\v4.0.30319"
} | Where-Object { Test-Path (Join-Path $_ 'csc.exe') } | Select-Object -First 1
if (!$framework) {throw 'Enable .NET Framework 4.8 before building.'}
$csc = Join-Path $framework 'csc.exe'
$version = (Get-Content (Join-Path $root VERSION) -Raw).Trim()
$stage = Join-Path ([IO.Path]::GetTempPath()) ('gloria-build-' + [guid]::NewGuid())
$null = New-Item -Type Directory $stage -Force
try {
    $flags = @('/nologo','/platform:anycpu','/optimize+','/codepage:65001', '/r:System.Web.Extensions.dll')
    $wpf = @('WindowsBase','PresentationCore','PresentationFramework') | ForEach-Object { "/r:$framework\WPF\$_.dll" }
    $sources = @('Events.cs','Character.cs','Desktop.cs','Hooks.cs','Pet.cs','Peek.cs','Courier.cs','AgentCalls.cs','ModelScene.cs','App.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
    & $csc @flags @wpf /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Xml.Linq.dll /r:System.Runtime.Serialization.dll /target:winexe "/win32icon:$PSScriptRoot\assets\GLORIA.ico" "/out:$stage\GLORIA.exe" @sources
    if ($LASTEXITCODE) { throw "GLORIA build failed ($LASTEXITCODE)" }
    & $csc @flags /target:exe "/out:$stage\GLORIA-Hook.exe" "$PSScriptRoot\Events.cs" "$PSScriptRoot\AgentCalls.cs" "$PSScriptRoot\HookMain.cs"
    if ($LASTEXITCODE) { throw "Hook build failed ($LASTEXITCODE)" }
    foreach ($id in @('gloria','doro','usagi','grokbot','fourteen')) {
        $pet = New-Item -Type Directory (Join-Path $stage "characters\$id") -Force
        Copy-Item -LiteralPath "$root\characters\$id\$id.json" -Destination $pet.FullName
        if ($id -eq 'fourteen') {
            Copy-Item -Path "$PSScriptRoot\assets\fourteen\*" -Destination $pet.FullName
        } elseif ($id -eq 'grokbot') {
            Copy-Item -LiteralPath "$PSScriptRoot\assets\grokbot.mesh","$PSScriptRoot\assets\grokbot.rig.json" -Destination $pet.FullName
        } else {
            Copy-Item -LiteralPath "$root\characters\$id\$id.png" -Destination $pet.FullName
        }
    }
    Copy-Item -LiteralPath "$PSScriptRoot\README.md","$root\LICENSE" -Destination $stage
    $null = New-Item -Type Directory $OutputDirectory -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $OutputDirectory "GLORIA-$version-Windows.zip"
    $tempZip = "$zip.$([guid]::NewGuid()).tmp"
    try {
        [IO.Compression.ZipFile]::CreateFromDirectory($stage,$tempZip)
        if ([IO.File]::Exists($zip)) {[IO.File]::Replace($tempZip,$zip,[System.Management.Automation.Language.NullString]::Value)} else {[IO.File]::Move($tempZip,$zip)}
    } finally {if (Test-Path $tempZip) {Remove-Item -LiteralPath $tempZip}}
    Write-Output $zip
} finally {Remove-Item -LiteralPath $stage -Force -Recurse}

# Renders the UI's demo screens offscreen with headless Edge (no window opens). Dev helper only; not shipped.
#   .\shots.ps1 [-Out <folder>] [-Screens main,split2,launch,error,setup,setup2,test]
#   split-screen: split3, split3full, split4, split4full, guest4, launch3, launch4, full4
param([string]$Out = "$PSScriptRoot\..\shots", [string[]]$Screens = @('main', 'split2', 'launch', 'error', 'setup', 'test'),
      [int]$Width = 1200, [int]$Height = 800)
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
New-Item -ItemType Directory -Force $Out | Out-Null
$page = 'file:///' + ((Resolve-Path "$PSScriptRoot\index.html").Path -replace '\\', '/')
foreach ($s in $Screens) {
    Start-Process -FilePath $edge -WindowStyle Hidden -Wait -ArgumentList '--headless=new', '--disable-gpu', '--hide-scrollbars',
        "--user-data-dir=$env:TEMP\unclecraft-shots-profile", "--window-size=$Width,$Height", '--virtual-time-budget=4000',
        "--screenshot=$Out\$s.png", "$page`?demo=$s" -RedirectStandardError "$env:TEMP\unclecraft-shots.err"
    "$s -> $Out\$s.png"
}

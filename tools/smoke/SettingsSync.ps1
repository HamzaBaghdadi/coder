param([string]$Exe, [string]$DataA, [string]$DataB, [string]$Shots)

# Real-window check of Settings Sync with a real GitHub account (needs the internet and the user):
# instance A signs in (the first time, finish the sign-in in the browser: the code is copied for you),
# zooms the editor and installs Coder Dusk; instance B, with its own --user-data-dir and the same
# sign-in, starts and must receive both. B gets A's token file, which only this Windows user can decrypt
# (DPAPI), so the code is entered once. Not part of Run.ps1: it writes to your settings gist.
#   .\SettingsSync.ps1 -Exe <Coder.exe> -DataA $env:TEMP\coder-sync-a -DataB $env:TEMP\coder-sync-b -Shots $env:TEMP\coder-sync-shots
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Stop'

function Wait-For([scriptblock]$condition, [int]$seconds = 30) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while (-not (& $condition) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
  return [bool](& $condition)
}

function Run-Command([string]$title) {
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' ">$title"
  Post-Enter
}

function Font-Size([string]$data) {
  (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).editorFontSize
}

$tokenA = Join-Path $DataA 'sync\github-token.bin'
Start-Coder $Exe $DataA $env:TEMP
try {
  Start-Sleep -Seconds 2
  if (-not (Test-Path $tokenA)) {
    Run-Command 'Sign in with GitHub to Sync Settings'
    Write-Host 'Click "Copy Code and Open GitHub" in Coder, then paste the code on GitHub and authorize (5 minutes).'
  }

  Check 'A signed in (token stored, encrypted)' (Wait-For { Test-Path $tokenA } 300)
  Check 'A synced once' (Wait-For { (Test-Path (Join-Path $DataA 'sync\state.json')) -and ((Get-Content (Join-Path $DataA 'sync\state.json') -Raw) -match '"gistId"') } 60)
  Check 'token not in settings.json' (-not ((Get-Content (Join-Path $DataA 'settings.json') -Raw -ErrorAction SilentlyContinue) -match 'gho_'))

  Run-Command 'Zoom In'
  Run-Command 'Zoom In'
  Invoke 'Extensions (Ctrl+Shift+X)'
  Set-Text 'Search extensions' 'dusk'
  if (Wait-For { $null -ne (Find 'Install Coder Dusk') } 60) { Invoke 'Install Coder Dusk' }
  Check 'A has Coder Dusk' (Wait-For { @(Get-ChildItem (Join-Path $DataA 'extensions') -Filter 'coder.dusk-*' -Directory -ErrorAction SilentlyContinue).Count -eq 1 } 60)
  Start-Sleep -Seconds 6 # pushed two seconds after the last change
  Shot 'sync-a'
}
finally {
  Stop-Coder
}

$sizeA = Font-Size $DataA
Check 'A saved a zoomed font size' ($sizeA -gt 14) "$sizeA"

New-Item -ItemType Directory -Force (Join-Path $DataB 'sync') | Out-Null
Copy-Item $tokenA (Join-Path $DataB 'sync\github-token.bin') -Force
Start-Coder $Exe $DataB $env:TEMP
try {
  Check 'B installed Coder Dusk from the synced list' (Wait-For { @(Get-ChildItem (Join-Path $DataB 'extensions') -Filter 'coder.dusk-*' -Directory -ErrorAction SilentlyContinue).Count -eq 1 } 90)
  Start-Sleep -Seconds 2
  Shot 'sync-b'
}
finally {
  Stop-Coder
}

$sizeB = Font-Size $DataB
Check 'B received the font size' ($sizeB -eq $sizeA) "A=$sizeA B=$sizeB"

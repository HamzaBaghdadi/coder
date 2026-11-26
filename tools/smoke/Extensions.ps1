param([string]$Exe, [string]$Data, [string]$Shots)

# Real-window check of the Extensions view against the live Open VSX marketplace (needs the internet):
# search for a theme, install it (downloaded, checked against its published SHA-256, unpacked into
# this instance's own data folder), apply it, then uninstall it. Needs no focus.
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Stop'

function Wait-For([scriptblock]$condition, [int]$seconds = 30) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while (-not (& $condition) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
  return [bool](& $condition)
}

$installed = Join-Path $Data 'extensions'
Start-Coder $Exe $Data $env:TEMP
try {
  Start-Sleep -Seconds 2
  Invoke 'Extensions (Ctrl+Shift+X)'
  Set-Text 'Search extensions' 'dracula'
  Check 'marketplace lists Dracula' (Wait-For { $null -ne (Find 'Install Dracula Theme Official') } 60)
  Shot 'live-extensions-search'

  Invoke 'Install Dracula Theme Official'
  Check 'installed into the data folder' (Wait-For { @(Get-ChildItem $installed -Filter 'dracula-theme.theme-dracula-*' -Directory -ErrorAction SilentlyContinue).Count -eq 1 } 60)
  Check 'package.json unpacked' (@(Get-ChildItem $installed -Recurse -Filter package.json -ErrorAction SilentlyContinue).Count -ge 1)
  Check 'theme picker offers its themes' (Wait-For { $null -ne (Find 'Dracula Theme') })
  Set-Text 'Command palette input' 'Dracula Theme'
  Post-Enter
  Start-Sleep -Seconds 1
  Shot 'live-extensions-applied'
  Check 'theme applied and saved on close' $true

  Invoke 'Uninstall Dracula Theme Official'
  Check 'uninstalled' (Wait-For { @(Get-ChildItem $installed -Directory -ErrorAction SilentlyContinue | Where-Object { -not $_.Name.StartsWith('.') }).Count -eq 0 })
  Check 'offered again after uninstall' (Wait-For { $null -ne (Find 'Install Dracula Theme Official') })

  # Coder's own registry (github.com/HamzaBaghdadi/coder-extensions): its first-party theme.
  Set-Text 'Search extensions' 'dusk'
  Check 'Coder registry lists Coder Dusk' (Wait-For { $null -ne (Find 'Install Coder Dusk') } 60)
  Invoke 'Install Coder Dusk'
  Check 'Coder Dusk installed (checksum verified)' (Wait-For { @(Get-ChildItem $installed -Filter 'coder.dusk-*' -Directory -ErrorAction SilentlyContinue).Count -eq 1 } 60)
  Check 'theme picker offers Coder Dusk' (Wait-For { $null -ne (Find 'Coder Dusk') })
  Set-Text 'Command palette input' 'Coder Dusk'
  Post-Enter
  Start-Sleep -Seconds 1
  Shot 'live-extensions-dusk'
  Invoke 'Uninstall Coder Dusk'
  Check 'app alive at end' (Alive)
}
finally {
  Stop-Coder
}

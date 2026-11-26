param([string]$Exe, [string]$Folder, [string]$Data, [string]$Shots)

# Real-window check of the light theme: switch through the command palette, show the terminal in it,
# and confirm the choice is saved and restored on the next start. Needs no focus.
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Stop'

function Run-Command([string]$text) {
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' $text
  Post-Enter
}

# Average brightness (0-255) of a patch of the editor area, from a capture of the window.
function Editor-Brightness() {
  Shot 'theme-probe'
  $bmp = [System.Drawing.Bitmap]::FromFile((Join-Path $Shots 'theme-probe.png'))
  try {
    $sum = 0; $n = 0
    foreach ($x in 900..940) { foreach ($y in 300..320) { $c = $bmp.GetPixel($x, $y); $sum += ($c.R + $c.G + $c.B) / 3; $n++ } }
    return $sum / $n
  } finally { $bmp.Dispose() }
}

Start-Coder $Exe $Data $Folder
try {
  Start-Sleep -Seconds 2
  $before = Editor-Brightness
  Check 'starts in the dark theme' ($before -lt 60) "brightness=$([int]$before)"

  Run-Command '>Color Theme'
  Start-Sleep -Seconds 1 # the picker lists VS Code's themes too, found in the background
  Set-Text 'Command palette input' 'Daylight'
  Post-Enter
  Start-Sleep -Milliseconds 500
  $after = Editor-Brightness
  Check 'switched to the light theme live' ($after -gt 200) "brightness=$([int]$after)"

  Run-Command '>Toggle Terminal'
  Start-Sleep -Seconds 3
  Shot 'live-theme-light'
}
catch {
  Check 'script' $false $_.Exception.Message
  Shot 'live-theme-error'
}
finally {
  Stop-Coder
}

$settings = Get-Content (Join-Path $Data 'settings.json') -Raw | ConvertFrom-Json
Check 'the choice was saved' ($settings.colorTheme -eq 'light') "colorTheme=$($settings.colorTheme)"

Start-Coder $Exe $Data $Folder
try {
  Start-Sleep -Seconds 3
  $restarted = Editor-Brightness
  Check 'starts light next time' ($restarted -gt 200) "brightness=$([int]$restarted)"

  # VS Code's own Monokai, when VS Code is installed: found, converted and applied live.
  if (Test-Path "$env:LOCALAPPDATA\Programs\Microsoft VS Code") {
    Run-Command '>Color Theme'
    Start-Sleep -Seconds 1
    Set-Text 'Command palette input' 'Monokai'
    Start-Sleep -Milliseconds 300
    Post-Enter
    Start-Sleep -Milliseconds 500
    $monokai = Editor-Brightness
    Shot 'live-theme-monokai'
    Check 'applied VS Code''s Monokai' ($monokai -lt 60) "brightness=$([int]$monokai)"
  }
}
finally {
  Stop-Coder
}

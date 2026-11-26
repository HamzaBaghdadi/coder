param([string]$Exe, [string]$Data, [string]$Shots)

# Real-window check of the web languages Coder ships: HTML tag completion accepted with Enter, and
# Format Document (Shift+Alt+F) with Prettier. The servers are copied from the test cache when there,
# so this doesn't download them every run (the first run installs them, which takes a few minutes).
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Stop'

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
  Check 'Node.js installed (skipped)' $true
  return
}

$cache = Join-Path ([IO.Path]::GetTempPath()) 'coder-test-servers'
if (Test-Path $cache) {
  New-Item -ItemType Directory -Force $Data | Out-Null
  Copy-Item $cache (Join-Path $Data 'servers') -Recurse -Force
}

$folder = Join-Path ([IO.Path]::GetTempPath()) ("coder-live-web-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $folder | Out-Null
$html = Join-Path $folder 'index.html'
$js = Join-Path $folder 'messy.js'
[IO.File]::WriteAllText($html, "<!DOCTYPE html>`n<html>`n<body>`n`n</body>`n</html>`n")
[IO.File]::WriteAllText($js, "const   a={b:1,   c:[1,2,3]}`n")

function Open-File([string]$name) {
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' $name
  Post-Enter
  Start-Sleep -Seconds 1
}

Start-Coder $Exe $Data $folder
try {
  Start-Sleep -Seconds 2
  Open-File 'index.html'
  # The HTML server starts (or installs, the first time); wait until the status bar says it runs.
  $deadline = (Get-Date).AddSeconds(90)
  while ((Get-Date) -lt $deadline -and $null -eq (Find 'vscode-html-language-server')) { Start-Sleep -Milliseconds 500 }
  Check 'HTML server running' ($null -ne (Find 'vscode-html-language-server'))

  # Line 4 is empty: type "<sec", accept the completion, save.
  Keys '^{HOME}{DOWN 3}'
  Keys '<sec'
  Start-Sleep -Seconds 2
  Shot 'live-web-completion'
  Keys '{ENTER}'
  Keys '^s'
  Start-Sleep -Seconds 1
  $text = [IO.File]::ReadAllText($html)
  Check 'HTML completion inserted <section>' ($text -match '<section') "line=$(($text -split "`n")[3])"

  Open-File 'messy.js'
  Keys '+%f'
  Start-Sleep -Seconds 6 # Prettier runs (installs the first time)
  Keys '^s'
  Start-Sleep -Seconds 1
  $formatted = [IO.File]::ReadAllText($js)
  Check 'Prettier formatted the file' ($formatted.Trim() -eq 'const a = { b: 1, c: [1, 2, 3] };') "file=$formatted"
  Shot 'live-web-prettier'
  Check 'app alive at end' (Alive)
}
catch {
  Check 'script' $false $_.Exception.Message
  Shot 'live-web-error'
}
finally {
  Stop-Coder
  Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue
}

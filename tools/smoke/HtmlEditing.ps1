param([string]$Exe, [string]$Data, [string]$Shots)

# Real-window regression check: in an HTML file, type after </html> and delete it with Backspace and
# Delete. This once threw in the middle of the edit and left the app frozen.
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Stop'

$folder = Join-Path ([IO.Path]::GetTempPath()) ("coder-live-html-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $folder | Out-Null
$file = Join-Path $folder 'index.html'
[IO.File]::WriteAllText($file, "<!DOCTYPE html>`n<html>`n<body>`n  <p>Hello</p>`n</body>`n</html>")

function Responsive() {
  $script:proc.Refresh()
  return (Alive) -and $script:proc.Responding
}

Start-Coder $Exe $Data $folder
try {
  Start-Sleep -Seconds 2
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' 'index.html'
  Post-Enter
  Start-Sleep -Seconds 1
  Keys '^{END}'
  Keys ' hello'
  Keys '{BACKSPACE 6}'
  Start-Sleep -Seconds 1
  Check 'backspace after </html> keeps the app responsive' (Responsive)
  Keys '{ENTER}hello{LEFT 5}{DELETE 5}{BACKSPACE}'
  Start-Sleep -Seconds 1
  Check 'delete after </html> keeps the app responsive' (Responsive)
  Keys '^s'
  Start-Sleep -Seconds 1
  $text = [IO.File]::ReadAllText($file)
  Check 'the typed text is gone' ($text.TrimEnd().EndsWith('</html>')) "end=$($text.Substring([Math]::Max(0, $text.Length - 20)))"
  Shot 'live-html-delete'

  # Built-in Emmet: an abbreviation typed in the body expands on Tab.
  Keys '^{HOME}{DOWN 3}{END}{ENTER}ul>li.item*2'
  Start-Sleep -Milliseconds 500
  Keys '{ESC}{TAB}'
  Start-Sleep -Milliseconds 500
  Shot 'live-html-emmet'
  Keys '^s'
  Start-Sleep -Seconds 1
  $text = [IO.File]::ReadAllText($file)
  Check 'Tab expands an Emmet abbreviation' ($text.Contains('<ul>') -and $text.Contains('<li class="item"></li>') -and -not $text.Contains('ul>li')) "text=$text"
}
catch {
  Check 'script' $false $_.Exception.Message
  Shot 'live-html-error'
}
finally {
  Stop-Coder
  Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue
}

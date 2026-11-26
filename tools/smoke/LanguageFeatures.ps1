param([string]$Exe, [string]$Data, [string]$Shots)

# Real-window check of rename and the Problems panel with gopls (skipped when it is not installed).
# Needs no focus: buttons are invoked, text is set through UI Automation, and plain keys (F2, End,
# Left, Enter) are posted to Coder's own window only.
. "$PSScriptRoot\SmokeCommon.ps1"

if (-not (Get-Command gopls -ErrorAction SilentlyContinue) -and -not (Test-Path "$HOME\go\bin\gopls.exe")) {
  Check 'gopls installed (skipped)' $true
  return
}

$workDir = Join-Path ([IO.Path]::GetTempPath()) ("coder-live-lsp-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $workDir | Out-Null
Set-Content (Join-Path $workDir 'go.mod') "module demo`n`ngo 1.21`n" -NoNewline
$main = Join-Path $workDir 'main.go'
$helper = Join-Path $workDir 'helper.go'
[IO.File]::WriteAllText($main, "package main`n`nimport `"os`"`n`nfunc main() {`n`tprintln(helper())`n}`n")
[IO.File]::WriteAllText($helper, "package main`n`nfunc helper() int { return 1 }`n")
$ErrorActionPreference = 'Stop'

function Post-Key([int]$vk) {
  [void][Win]::PostMessage($script:hwnd, 0x0100, [IntPtr]$vk, [IntPtr]0)
  [void][Win]::PostMessage($script:hwnd, 0x0101, [IntPtr]$vk, [IntPtr]0)
  Start-Sleep -Milliseconds 300
}

Start-Coder $Exe $Data $workDir
try {
  Start-Sleep -Seconds 2
  # Open main.go through quick open (the title bar's search box).
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' 'main.go'
  Post-Enter
  Start-Sleep -Seconds 6 # gopls loads the package

  # Problems: the unused import is listed.
  Invoke 'Problems'
  Start-Sleep -Seconds 1
  $problem = Find 'Error: "os" imported and not used [Ln 3, Col 8]'
  Check 'problems panel lists the unused import' ($null -ne $problem)
  Shot 'live-problems'

  # Rename helper to compute: caret after "helper" on line 6, F2, type the name, Enter.
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' ':6'
  Post-Enter
  Post-Key 0x23 # End
  foreach ($i in 1..3) { Post-Key 0x25 } # Left x3: "helper|())"
  Post-Key 0x71 # F2
  Set-Text 'Command palette input' 'compute'
  Shot 'live-rename-box'
  Post-Enter
  Start-Sleep -Seconds 2
  $helperText = [IO.File]::ReadAllText($helper)
  Check 'rename changed the closed file on disk' ($helperText -match 'func compute\(\) int') "helper.go=$helperText"
  $script:proc.Refresh()
  Check 'rename left the open file unsaved for review' ($script:proc.MainWindowTitle -like 'main.go*') "title=$($script:proc.MainWindowTitle)"
  Shot 'live-rename'

  # Find All References on the renamed function: the References panel lists both uses with their lines.
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' '>Find All References'
  Post-Enter
  Start-Sleep -Seconds 2
  Check 'references panel lists the call' ($null -ne (Find 'main.go line 6: println(compute())'))
  Check 'references panel lists the declaration' ($null -ne (Find 'helper.go line 3: func compute() int { return 1 }'))
  Check 'references title names the symbol' ($null -ne (Find '2 references to "compute" in 2 files'))
  Shot 'live-references'

  # Go to Symbol in Workspace (#): the function is found by name.
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' '#comp'
  Start-Sleep -Seconds 2
  Check 'workspace symbol search finds the function' ($null -ne (Find 'compute'))
  Shot 'live-workspace-symbols'
  Post-Key 0x1B # Esc
  Check 'app alive at end' (Alive)
}
catch {
  Check 'script' $false $_.Exception.Message
  Shot 'live-lsp-error'
}
finally {
  Stop-Coder
  Remove-Item -Recurse -Force $workDir -ErrorAction SilentlyContinue
}

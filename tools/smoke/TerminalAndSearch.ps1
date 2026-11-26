param([string]$Exe, [string]$Folder, [string]$Data, [string]$Shots)

# Real-window check of the terminal panel (several shells, killing them) and of the search view
# when the side bar collapses. Drives a separate Coder instance with its own --user-data-dir.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\SmokeCommon.ps1"

function Tabs() {
  $all = $script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  return @($all | Where-Object { $_.Current.Name -like 'Terminal *:*' })
}

Start-Coder $Exe $Data $Folder
try {
  Start-Sleep -Seconds 2
  # Terminal: open, add two more, kill some, check the app survives each step.
  Keys '^`'
  Start-Sleep -Seconds 2
  Check 'terminal opened with one tab' ((Tabs).Count -eq 1) "tabs=$((Tabs).Count)"
  Invoke 'New Terminal (Ctrl+Shift+`)'
  Invoke 'New Terminal (Ctrl+Shift+`)'
  Start-Sleep -Seconds 2
  Check 'new terminal keeps app alive' (Alive)
  Check 'three terminal tabs' ((Tabs).Count -eq 3) "tabs=$(((Tabs) | ForEach-Object { $_.Current.Name }) -join ', ')"
  Keys 'echo coder-live-check{ENTER}'
  Start-Sleep -Seconds 1
  Shot 'live-terminals'
  Invoke 'Kill Terminal'
  Start-Sleep -Seconds 2
  Check 'kill keeps app alive' (Alive)
  Check 'two tabs after kill' ((Tabs).Count -eq 2)
  Invoke 'Kill Terminal 1'
  Start-Sleep -Seconds 2
  Check 'tab close keeps app alive' (Alive)
  Check 'one tab left' ((Tabs).Count -eq 1)
  $last = (Tabs)[0].Current.Name
  Invoke 'Kill Terminal'
  Start-Sleep -Seconds 2
  Check 'killing the last terminal keeps app alive' (Alive)
  Check 'panel hidden after last kill' ($null -eq (Find 'Kill Terminal')) "last was $last"
  Keys '^`'
  Start-Sleep -Seconds 2
  Check 'reopening starts a fresh terminal' ((Tabs).Count -eq 1)
  Invoke 'Split Terminal (Ctrl+Shift+5)'
  Start-Sleep -Seconds 2
  Check 'split adds a terminal beside the first' ((Tabs).Count -eq 2) "tabs=$((Tabs).Count)"
  Check 'split keeps app alive' (Alive)
  Shot 'live-terminal-split'
  Keys '^f'
  Start-Sleep -Milliseconds 500
  $finder = Find 'Find in terminal'
  Check 'Ctrl+F in the terminal opens its find bar' ($null -ne $finder -and -not $finder.Current.IsOffscreen)
  Keys 'Coder'
  Start-Sleep -Milliseconds 500
  Shot 'live-terminal-find'
  Keys '{ESC}'

  # Search: hide the terminal (it owns the keys while focused), open search, collapse the side bar.
  Keys '^`'
  Keys '^+f'
  Check 'search box visible before collapse' ($null -ne (Find 'Match Case (Alt+C)'))
  Keys 'abc'
  Shot 'live-search-open'
  Keys '^b'
  Shot 'live-search-collapsed'
  $toggle = Find 'Match Case (Alt+C)'
  Check 'search box hidden with side bar' ($null -eq $toggle -or $toggle.Current.IsOffscreen) "found=$($null -ne $toggle)"
  Keys '^b'
  Check 'app alive at end' (Alive)
}
catch {
  Check 'script' $false $_.Exception.Message
  Shot 'live-error'
}
finally {
  Stop-Coder
}

param([string]$Exe, [string]$Data, [string]$Shots)

# Real-window Source Control check on a throwaway repository.
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\SmokeCommon.ps1"

$repo = Join-Path ([IO.Path]::GetTempPath()) ("coder-live-git-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $repo | Out-Null
git -C $repo init -q -b main; git -C $repo config user.email t@example.com; git -C $repo config user.name Test
Set-Content (Join-Path $repo 'app.cs') 'class App {}'
git -C $repo add -A; git -C $repo commit -q -m initial
Set-Content (Join-Path $repo 'app.cs') 'class App { int x; }'
Set-Content (Join-Path $repo 'new file.txt') 'hello'

Start-Coder $Exe $Data $repo
try {
  Start-Sleep -Seconds 3
  Check 'branch shown in status bar' ($null -ne (Find 'Branch'))
  Keys '^+g'
  Check 'modified file listed' ($null -ne (Find 'app.cs, Modified'))
  Check 'untracked file listed' ($null -ne (Find 'new file.txt, Untracked'))
  Shot 'live-scm-before'

  # Enter on a change opens its diff as a tab (the window title follows the active tab).
  $row = Find 'app.cs, Modified'
  $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $row.SetFocus()
  Keys '{ENTER}'
  Start-Sleep -Seconds 1
  $script:proc.Refresh()
  Check 'diff tab opened' ($script:proc.MainWindowTitle -like 'app.cs (Working Tree)*') "title=$($script:proc.MainWindowTitle)"
  Shot 'live-diff'
  Keys '{F7}'
  Check 'F7 in the diff keeps app alive' (Alive)
  Keys '^w'

  # Explorer: Enter on a file row opens it.
  Keys '^+e'
  $file = Find 'app.cs'
  $file.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $file.SetFocus()
  Keys '{ENTER}'
  $script:proc.Refresh()
  Check 'explorer Enter opens the file' ($script:proc.MainWindowTitle -like 'app.cs *' -and $script:proc.MainWindowTitle -notlike '*Working Tree*') "title=$($script:proc.MainWindowTitle)"
  Keys '^w'
  Keys '^+g'
  Invoke 'Stage app.cs'
  Start-Sleep -Seconds 1
  Check 'stage moved file to staged' ($null -ne (Find 'Unstage app.cs'))
  $box = Find 'Commit message'
  $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Live commit from Coder')
  Invoke 'Commit'
  Start-Sleep -Seconds 2
  $log = (git -C $repo log -1 --format=%s)
  Check 'commit landed in git' ($log -eq 'Live commit from Coder') "log=$log"
  Check 'untracked file still a change' ((git -C $repo status --porcelain) -match 'new file.txt')
  Start-Sleep -Seconds 1
  Check 'graph lists the new commit' ($null -ne (Find 'Live commit from Coder, Test, just now'))
  Check 'graph lists the first commit' ($null -ne (Find 'initial, Test, just now'))
  Shot 'live-scm-after'
  Check 'app alive at end' (Alive)
}
catch {
  Check 'script' $false $_.Exception.Message
  Shot 'live-scm-error'
}
finally {
  Stop-Coder
  Get-ChildItem $repo -Recurse -File -Force | ForEach-Object { $_.Attributes = 'Normal' }
  Remove-Item -Recurse -Force $repo
}

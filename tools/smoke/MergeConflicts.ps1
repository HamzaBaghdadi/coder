param([string]$Exe, [string]$Data, [string]$Shots)

# Real-window check of merge conflict resolution on a throwaway repository: open the conflicted file
# from Source Control, accept a side with the button on the conflict, save, stage and commit the merge.
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Continue' # Run.ps1's 'Stop' is inherited; the failing merge below is expected

$repo = Join-Path ([IO.Path]::GetTempPath()) ("coder-live-merge-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$file = Join-Path $repo 'app.txt'
New-Item -ItemType Directory $repo | Out-Null
git -C $repo init -q -b main; git -C $repo config user.email t@example.com; git -C $repo config user.name Test
git -C $repo config core.autocrlf false
[IO.File]::WriteAllText($file, "top`nvalue = 1`nbottom`n")
git -C $repo add -A; git -C $repo commit -q -m initial
git -C $repo checkout -q -b feature
[IO.File]::WriteAllText($file, "top`nvalue = 3`nbottom`n")
git -C $repo commit -q -am three
git -C $repo checkout -q main
[IO.File]::WriteAllText($file, "top`nvalue = 2`nbottom`n")
git -C $repo commit -q -am two
git -C $repo merge feature 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'

Start-Coder $Exe $Data $repo
try {
  Start-Sleep -Seconds 3
  Keys '^+g'
  $row = Find 'app.txt, Conflicted'
  Check 'conflicted file listed' ($null -ne $row)
  $message = (Find 'Commit message').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
  Check 'commit box holds the merge message' ($message -like "Merge branch 'feature'*") "message=$message"

  $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $row.SetFocus()
  Keys '{ENTER}'
  Start-Sleep -Seconds 1
  Check 'accept buttons shown on the conflict' ($null -ne (Find 'Accept Incoming Change'))
  Shot 'live-merge-conflict'

  Invoke 'Accept Incoming Change'
  Keys '^s'
  Start-Sleep -Seconds 1
  $text = [IO.File]::ReadAllText($file)
  Check 'incoming side kept, markers gone' ($text -eq "top`nvalue = 3`nbottom`n") "file=$text"
  Check 'buttons gone once resolved' ($null -eq (Find 'Accept Incoming Change'))

  Keys '^+g'
  Invoke 'Stage app.txt'
  Start-Sleep -Seconds 1
  Invoke 'Commit'
  Start-Sleep -Seconds 2
  $parents = (git -C $repo log -1 --format=%P).Trim().Split(' ').Count
  Check 'merge committed' ($parents -eq 2) "parents=$parents"
  Shot 'live-merge-done'
  Check 'app alive at end' (Alive)
}
catch {
  Check 'script' $false $_.Exception.Message
  Shot 'live-merge-error'
}
finally {
  Stop-Coder
  Get-ChildItem $repo -Recurse -File -Force | ForEach-Object { $_.Attributes = 'Normal' }
  Remove-Item -Recurse -Force $repo
}

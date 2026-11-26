param([string]$Exe, [string]$Data, [string]$Shots)

# Real-window check of branches and sync. A bare repository in the temp folder is the remote,
# so publishing and switching run for real without touching any real server.
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Continue' # Run.ps1's 'Stop' is inherited; git's stderr warnings during setup are not errors

$workDir = Join-Path ([IO.Path]::GetTempPath()) ("coder-live-branches-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$remote = Join-Path $workDir 'remote.git'
$repo = Join-Path $workDir 'work'
New-Item -ItemType Directory $workDir | Out-Null
git init -q --bare -b main $remote
git clone -q $remote $repo 2>$null
git -C $repo config user.email t@example.com; git -C $repo config user.name Test
Set-Content (Join-Path $repo 'a.txt') 'hello'
git -C $repo add -A; git -C $repo commit -q -m first
$ErrorActionPreference = 'Stop' # only now: git's warnings on stderr during setup are not errors

Start-Coder $Exe $Data $repo
try {
  Start-Sleep -Seconds 3
  # Publishing lives in Source Control (the status bar only shows the branch).
  Invoke 'Source Control (Ctrl+Shift+G)'
  Start-Sleep -Seconds 1
  Check 'source control offers publishing' ($null -ne (Find 'Publish Branch'))
  Invoke 'Publish Branch'
  Start-Sleep -Seconds 2
  $published = git -C $repo ls-remote --heads origin main
  Check 'publish pushed main to the remote' ($published -match 'refs/heads/main') "ls-remote=$published"

  # Branch picker: click the branch, type a new name, Enter creates and switches.
  Invoke 'Branch'
  Set-Text 'Command palette input' 'live test'
  Shot 'live-branch-picker'
  Post-Enter
  Start-Sleep -Seconds 1
  $current = git -C $repo branch --show-current
  Check 'picker created and switched to live-test' ($current -eq 'live-test') "branch=$current"

  # And back to main by name.
  Invoke 'Branch'
  Set-Text 'Command palette input' 'main'
  Post-Enter
  Start-Sleep -Seconds 1
  $current = git -C $repo branch --show-current
  Check 'picker switched back to main' ($current -eq 'main') "branch=$current"
  Shot 'live-branches'
  Check 'app alive at end' (Alive)
}
catch {
  Check 'script' $false $_.Exception.Message
  Shot 'live-branches-error'
}
finally {
  Stop-Coder
  Get-ChildItem $workDir -Recurse -File -Force | ForEach-Object { $_.Attributes = 'Normal' }
  Remove-Item -Recurse -Force $workDir
}

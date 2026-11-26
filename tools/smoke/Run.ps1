# Builds Coder and runs every real-window smoke check against a separate instance.
# Your own settings, recent folders and logs are untouched: the instance uses its own --user-data-dir.
# The window comes to the front while keys are sent; do not type during the run (about a minute).
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\..\.."
$work = Join-Path ([IO.Path]::GetTempPath()) 'coder-smoke'
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
dotnet build "$repo\src\Coder.App" --artifacts-path "$work\build" -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$exe = "$work\build\bin\Coder.App\debug\Coder.exe"
$results = @()
$results += & "$PSScriptRoot\TerminalAndSearch.ps1" -Exe $exe -Folder $repo -Data "$work\data-terminal" -Shots "$work\shots"
$results += & "$PSScriptRoot\SourceControl.ps1" -Exe $exe -Data "$work\data-git" -Shots "$work\shots"
$results += & "$PSScriptRoot\Branches.ps1" -Exe $exe -Data "$work\data-branches" -Shots "$work\shots"
$results += & "$PSScriptRoot\MergeConflicts.ps1" -Exe $exe -Data "$work\data-merge" -Shots "$work\shots"
$results += & "$PSScriptRoot\LanguageFeatures.ps1" -Exe $exe -Data "$work\data-lsp" -Shots "$work\shots"
$results += & "$PSScriptRoot\WebLanguages.ps1" -Exe $exe -Data "$work\data-web" -Shots "$work\shots"
$results += & "$PSScriptRoot\HtmlEditing.ps1" -Exe $exe -Data "$work\data-html" -Shots "$work\shots"
$results += & "$PSScriptRoot\ScrollBarDrag.ps1" -Exe $exe -Folder $repo -Data "$work\data-scroll" -Shots "$work\shots" # moves the real mouse
$results += & "$PSScriptRoot\Extensions.ps1" -Exe $exe -Data "$work\data-extensions" -Shots "$work\shots" # needs the internet (Open VSX)
$results += & "$PSScriptRoot\WindowChrome.ps1" -Exe $exe -Folder $repo -Data "$work\data-chrome" -Shots "$work\shots"
$results += & "$PSScriptRoot\Theme.ps1" -Exe $exe -Folder $repo -Data "$work\data-theme" -Shots "$work\shots"
$results
$failed = @($results | Where-Object { "$_" -like 'FAIL*' }).Count
"`n$($results.Count - $failed) passed, $failed failed. Screenshots: $work\shots"
exit $failed

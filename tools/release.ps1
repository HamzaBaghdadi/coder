# Builds a Windows release of Coder and (with -Publish) puts it on GitHub Releases, where the installed
# app finds it and updates itself in the background.
#
#   ./tools/release.ps1                 # build artifacts/releases/Coder-win-Setup.exe and the update packages
#   ./tools/release.ps1 -Publish        # also create the GitHub release v<version> with them
#
# The version is <Version> in Directory.Build.props: raise it before each release. Velopack (vpk) makes
# the installer, the full package and, from the previous release, a delta package (only what changed).
# The release workflow (.github/workflows/release.yml) runs this same script when a v* tag is pushed.
param([switch]$Publish, [string]$Repository = 'https://github.com/HamzaBaghdadi/coder')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
[xml]$props = Get-Content "$root\Directory.Build.props"
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

$publishDir = "$root\artifacts\publish\win-x64"
$releasesDir = "$root\artifacts\releases"
Remove-Item -Recurse -Force $publishDir, $releasesDir -ErrorAction SilentlyContinue

Write-Output "Building Coder $version (win-x64, self-contained)"
dotnet publish "$root\src\Coder.App" -c Release -r win-x64 --self-contained -o $publishDir -p:Version=$version -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

dotnet tool restore | Out-Null
$token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { (gh auth token) }

# The previous release, so vpk can make a delta package (fine to skip on the first release).
dotnet tool run vpk download github --repoUrl $Repository --token $token -o $releasesDir 2>$null
if ($LASTEXITCODE -ne 0) { Write-Output 'No previous release found: this one ships a full package only.' }

dotnet tool run vpk pack --packId Coder --packVersion $version --packDir $publishDir --mainExe Coder.exe `
  --packTitle Coder --packAuthors 'Hamza Baghdadi' --icon "$root\assets\coder.ico" -o $releasesDir
if ($LASTEXITCODE -ne 0) { throw 'Packing failed.' }
Get-ChildItem $releasesDir | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize

if ($Publish) {
  dotnet tool run vpk upload github --repoUrl $Repository --token $token -o $releasesDir `
    --publish --releaseName "Coder $version" --tag "v$version"
  if ($LASTEXITCODE -ne 0) { throw 'Upload failed.' }
  Write-Output "Published: $Repository/releases/tag/v$version"
}

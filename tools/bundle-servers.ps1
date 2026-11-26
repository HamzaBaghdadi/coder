param(
  # The folder of the built app (where Coder.exe is). The servers go into its "servers" folder.
  [Parameter(Mandatory = $true)][string]$AppFolder,
  # The Node.js to ship with them; by default the one on PATH. Use an LTS release (20 or newer).
  [string]$NodeExe = (Get-Command node -ErrorAction Stop).Source
)

# Bundles the language servers and tools Coder ships (HTML, CSS, JSON, TypeScript/JavaScript, Tailwind CSS
# IntelliSense, Prettier) with Node.js into <app>/servers, so a release works with no internet and no
# Node.js on the machine. Without this folder Coder installs each one with npm on first use instead.
# The packages and versions must match ManagedTools in src/Coder.Core/Lsp/ManagedServers.cs (a test checks).
$ErrorActionPreference = 'Stop'

$packages = @(
  'vscode-langservers-extracted@4.10.0',
  'typescript-language-server@5.3.0',
  'typescript@5.9.3',
  '@tailwindcss/language-server@0.16.0',
  'prettier@3.10.1'
)

$servers = Join-Path (Resolve-Path $AppFolder) 'servers'
New-Item -ItemType Directory -Force (Join-Path $servers 'node') | Out-Null

Write-Host "Installing $($packages.Count) packages into $servers ..."
npm install --prefix $servers --no-audit --no-fund --loglevel=error --save-exact @packages
if ($LASTEXITCODE -ne 0) { throw "npm install failed ($LASTEXITCODE)." }

Copy-Item $NodeExe (Join-Path $servers 'node') -Force
$size = (Get-ChildItem $servers -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Bundled into {0} ({1:N0} MB, Node.js {2})." -f $servers, $size, (& $NodeExe --version))

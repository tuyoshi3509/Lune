param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.-]+)?$') { throw 'Invalid version' }
Set-Location -LiteralPath $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1')
$staging = Join-Path $PSScriptRoot ('release/package-' + [Guid]::NewGuid().ToString('N'))
$folder = Join-Path $staging 'Lune'
$null = New-Item -ItemType Directory -Path $folder
# Explicit allowlist: no user state, music, cached covers, logs or downloaded tools.
foreach ($name in @('Lune.exe', 'lune.portable', 'Luneを起動.cmd', 'README.md', 'CONVENIENCE.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $folder
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $folder -Recurse
$archive = Join-Path $PSScriptRoot "release/Lune-$Version-win-x64.zip"
if (Test-Path -LiteralPath $archive) { throw 'Archive already exists; choose a new version or preserve the old archive before packaging.' }
Compress-Archive -LiteralPath $folder -DestinationPath $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $PSScriptRoot "release/Lune-$Version-SHA256SUMS.txt"), "$hash  Lune-$Version-win-x64.zip`n", [Text.UTF8Encoding]::new($false))
Write-Host "Package: $archive"

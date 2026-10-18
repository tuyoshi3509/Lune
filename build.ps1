$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet restore .\Lune\Lune.csproj --configfile .\NuGet.Config -r win-x64 -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw '依存ライブラリの復元に失敗しました。' }
dotnet publish .\Lune\Lune.csproj -c Release --no-restore -p:PublishProfile=Exe -o .\release
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }
Copy-Item -LiteralPath '.\release\Lune.exe' -Destination '.\Lune.exe' -Force
Write-Host '完了: Lune.exe をダブルクリックして起動できます。'

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
dotnet build .\Lune\Lune.csproj -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。先に build.ps1 を実行してください。' }
$luneTest = Start-Process -FilePath '.\Lune\bin\Release\net10.0-windows\Lune.exe' -ArgumentList '--self-test --data test-output\unit-data' -PassThru -WindowStyle Hidden
if (-not $luneTest.WaitForExit(60000)) { throw 'テストがタイムアウトしました。' }
Get-Content -LiteralPath '.\test-output\self-tests.txt' -Encoding UTF8
if ($luneTest.ExitCode -ne 0) { throw 'テストに失敗しました。' }

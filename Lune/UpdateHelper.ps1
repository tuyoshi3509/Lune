param([Parameter(Mandatory=$true)][string]$Manifest)
$ErrorActionPreference = 'Stop'
$plan = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$stage = [IO.Path]::GetFullPath($plan.StageDirectory)
$root = [IO.Path]::GetFullPath($plan.InstallDirectory)
$result = Join-Path $stage 'result.json'
$applied = [Collections.Generic.List[object]]::new()
$newProcess = $null
$originalExited = $false
function Assert-PlainPath([string]$path) {
    $cursor = [IO.Path]::GetFullPath($path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '更新先にリンクがあるため中止しました。実体のフォルダーから起動してください。' }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }; $cursor = $parent
    }
}
function Launch-Lune([bool]$receipt) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $root 'Lune.exe'; $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.Arguments = '--data "' + $plan.DataDirectory + '"'
    if ($receipt) { $start.Arguments += ' --update-receipt ' + $plan.Receipt }
    return [Diagnostics.Process]::Start($start)
}
try {
    if ($plan.Receipt -notmatch '^[a-f0-9]{32}$' -or $plan.Version -notmatch '^v?\d+\.\d+\.\d+$') { throw 'Invalid update plan' }
    if ($stage -ne [IO.Path]::GetFullPath((Join-Path $plan.DataDirectory ('updates/' + $plan.Receipt)))) { throw 'Unexpected staging directory' }
    if ([IO.Path]::GetFullPath($Manifest) -ne (Join-Path $stage 'plan.json')) { throw 'Unexpected manifest' }
    Assert-PlainPath $root; Assert-PlainPath $stage
    $allowed = @('Lune.exe','lune.portable','Luneを起動.cmd','README.md','CONVENIENCE.md','THIRD-PARTY-NOTICES.md','licenses/Deno-LICENSE.txt','licenses/NAudio-LICENSE.txt','licenses/TagLibSharp-COPYING.txt','licenses/yt-dlp-LICENSE.txt')
    $seen = @{}
    foreach ($file in $plan.Files) {
        if ($file.Name -cnotin $allowed -or $seen.ContainsKey($file.Name)) { throw 'Unexpected update file' }; $seen[$file.Name] = $true
        $source = Join-Path $stage ('files/' + $file.Name); $target = Join-Path $root $file.Name
        Assert-PlainPath $source; Assert-PlainPath $target
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.Hash) { throw 'Prepared file checksum mismatch' }
    }
    if (-not $seen.ContainsKey('Lune.exe') -or -not $seen.ContainsKey('lune.portable')) { throw 'Incomplete update' }
    $old = Get-Process -Id $plan.ProcessId -ErrorAction SilentlyContinue
    if ($old -and $old.StartTime.ToUniversalTime().Ticks -eq $plan.ProcessStartTicks) {
        if (-not $old.WaitForExit(30000)) { throw 'Luneが終了しなかったため更新を中止しました。' }
    }
    $originalExited = $true
    foreach ($file in $plan.Files) {
        $target = Join-Path $root $file.Name
        $backup = Join-Path $stage ('backup/' + $file.Name)
        $existed = Test-Path -LiteralPath $target
        $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force
        if ($existed) { $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backup)) -Force; Copy-Item -LiteralPath $target -Destination $backup }
        $temp = $target + '.update-new'
        if (Test-Path -LiteralPath $temp) { throw 'Previous temporary update file exists' }
        Copy-Item -LiteralPath (Join-Path $stage ('files/' + $file.Name)) -Destination $temp
        $applied.Add([pscustomobject]@{ Target=$target; Backup=$backup; Existed=$existed })
        if ($existed) { [IO.File]::Replace($temp, $target, $null) } else { [IO.File]::Move($temp, $target) }
    }
    $newProcess = Launch-Lune $true
    $ack = Join-Path $stage 'ack.json'; $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $ack) -and $clock.Elapsed.TotalSeconds -lt 30 -and -not $newProcess.HasExited) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path -LiteralPath $ack)) { throw '更新後の起動を確認できませんでした。' }
    $confirmation = Get-Content -LiteralPath $ack -Raw | ConvertFrom-Json
    if ($confirmation.Version -ne $plan.Version.TrimStart('v') -or $confirmation.ProcessId -ne $newProcess.Id) { throw '更新後のバージョンが一致しません。' }
    @{ Success=$true; Version=$plan.Version; ProcessId=$newProcess.Id; Message='更新が完了しました。' } | ConvertTo-Json | Set-Content -LiteralPath $result -Encoding UTF8
    # Keep only the small receipt. No user data is touched; these are validated staging subfolders.
    foreach ($name in @('files','backup')) {
        $cleanup = [IO.Path]::GetFullPath((Join-Path $stage $name))
        if (-not $cleanup.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid cleanup path' }
        try { if (Test-Path -LiteralPath $cleanup) { Remove-Item -LiteralPath $cleanup -Recurse -Force } } catch { }
    }
}
catch {
    $message = $_.Exception.Message
    $canRestore = $true
    if ($newProcess -and -not $newProcess.HasExited) { $null = $newProcess.CloseMainWindow(); $canRestore = $newProcess.WaitForExit(10000) }
    if ($canRestore) {
        for ($i = $applied.Count - 1; $i -ge 0; $i--) {
            $file = $applied[$i]
            try { if ($file.Existed) { Copy-Item -LiteralPath $file.Backup -Destination $file.Target -Force } elseif (Test-Path -LiteralPath $file.Target) { Remove-Item -LiteralPath $file.Target -Force } }
            catch { $message += ' 復元エラー: ' + $_.Exception.Message }
        }
    }
    @{ Success=$false; Version=$plan.Version; Message=$message; BackupDirectory=(Join-Path $stage 'backup') } | ConvertTo-Json | Set-Content -LiteralPath $result -Encoding UTF8
    if ($originalExited -and $canRestore) { try { $null = Launch-Lune $false } catch { } }
}

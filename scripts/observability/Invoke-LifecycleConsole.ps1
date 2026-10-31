param([string]$Dir,[string]$Repo,[string]$Nonce)
$ErrorActionPreference = 'Stop'
# Scratch is a local operational safeguard, not protection against filesystem adversaries.
try {
    if ($Nonce -notmatch '^[a-f0-9]{32}$') { exit 1 }
    $root = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Temp\opencode\MVP41-lifecycle'))
    $dirFull = [IO.Path]::GetFullPath($Dir).TrimEnd('\')
    $cycle = Split-Path $dirFull -Leaf
    $run = Split-Path (Split-Path $dirFull -Parent) -Leaf
    if ($cycle -notmatch '^cycle[12]$' -or $run -notmatch '^run-[A-Za-z0-9]+$' -or
        (Split-Path (Split-Path $dirFull -Parent) -Parent) -ne $root) { exit 1 }
    $item = Get-Item -LiteralPath $dirFull -Force
    while ($item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 1 }
        $item = $item.Parent
    }
    $handshake = Join-Path $dirFull 'owner.txt'
    $handshakeItem = Get-Item -LiteralPath $handshake -Force
    if (($handshakeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        [IO.File]::ReadAllText($handshake) -cne $Nonce) { exit 1 }
    foreach ($name in @('out.txt','err.txt','pid.txt','signal-result.json','helper-result.json')) {
        if (Test-Path -LiteralPath (Join-Path $dirFull $name)) { exit 1 }
    }
} catch { exit 1 }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LifecycleConsole {
 [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AllocConsole();
 [DllImport("kernel32.dll", SetLastError=true)] public static extern bool FreeConsole();
 [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
 [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
}
'@
[LifecycleConsole]::FreeConsole() | Out-Null
if (-not [LifecycleConsole]::AllocConsole()) { exit 1 }
$p = $null; $failed = $false; $retained = $false
try {
    $p = Start-Process powershell.exe -ArgumentList @('-NoProfile','-File',('"' + (Join-Path $Repo 'scripts\observability\Start-LocalObservability.ps1') + '"')) -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $dirFull 'out.txt') -RedirectStandardError (Join-Path $dirFull 'err.txt')
    if (-not [LifecycleConsole]::SetConsoleCtrlHandler([IntPtr]::Zero,$true)) { throw 'handler' }
    [IO.File]::WriteAllText((Join-Path $dirFull 'pid.txt'),[string]$p.Id)
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    while (-not (Test-Path (Join-Path $dirFull 'signal')) -and -not $p.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200; $p.Refresh() }
    if (-not $p.HasExited) {
        $ok = [LifecycleConsole]::GenerateConsoleCtrlEvent(0,0)
        [IO.File]::WriteAllText((Join-Path $dirFull 'signal-result.json'),('{"event":0,"success":' + $ok.ToString().ToLowerInvariant() + '}'))
        if (-not $ok) { $failed = $true }
    } else { $failed = $true }
} catch { $failed = $true }
if ($p -and -not $p.WaitForExit(30000)) {
    $failed = $true; $retained = $true
    try { [IO.File]::WriteAllText((Join-Path $dirFull 'helper-result.json'),'{"status":"FAIL","retained":true,"logsSanitized":false}') } catch {}
    # Closing this console while the child is alive is not a normal shutdown.
    $p.WaitForExit()
}
if ($p) {
    # Only the runner can release the console after checking its full snapshot.
    $releaseDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path (Join-Path $dirFull 'done')) -and [DateTime]::UtcNow -lt $releaseDeadline) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path (Join-Path $dirFull 'done'))) {
        $failed = $true; $retained = $true
        try { [IO.File]::WriteAllText((Join-Path $dirFull 'helper-result.json'),'{"status":"FAIL","retained":true,"logsSanitized":false}') } catch {}
        while (-not (Test-Path (Join-Path $dirFull 'done'))) { Start-Sleep -Milliseconds 1000 }
    }
}
$sanitized = $true
foreach ($name in @('out.txt','err.txt')) {
    $file = Join-Path $dirFull $name
    try { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }; if (Test-Path -LiteralPath $file) { $sanitized = $false } } catch { $sanitized = $false }
}
if (-not $sanitized) { $failed = $true }
try {
    $status = if ($failed) { 'FAIL' } else { 'PASS' }
    $json = '{"status":"' + $status + '","retained":' + $retained.ToString().ToLowerInvariant() + ',"logsSanitized":' + $sanitized.ToString().ToLowerInvariant() + '}'
    [IO.File]::WriteAllText((Join-Path $dirFull 'helper-result.json'),$json)
} catch { $failed = $true }
if ($failed) { exit 1 }
exit 0

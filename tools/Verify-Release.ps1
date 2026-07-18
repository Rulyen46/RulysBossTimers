<#
Run this on bin\Release\ErenshorBossTimers.dll immediately before uploading it
as a GitHub Release asset.

It answers one question: is this file the PUBLIC build, or did a Debug build
end up in bin\Release? The dev build compiles in the fight recorder, which
writes every chat line your group types to disk. Shipping that by accident is
the failure this script exists to prevent.

Usage:  powershell -ExecutionPolicy Bypass -File tools\Verify-Release.ps1
#>

$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot '..\bin\Release\ErenshorBossTimers.dll'
if (-not (Test-Path $dll)) {
    Write-Host "FAIL: $dll not found. Run: dotnet build -c Release" -ForegroundColor Red
    exit 1
}
$dll = (Resolve-Path $dll).Path

# .NET metadata stores member and type names as UTF-8, so a plain byte scan for
# these names is a reliable presence test - no reflection or Cecil needed.
$bytes = [System.IO.File]::ReadAllBytes($dll)
$text  = [System.Text.Encoding]::UTF8.GetString($bytes)

$devMarkers = 'FightRecorder', 'Command_Record', 'Command_Dump'
$found = $devMarkers | Where-Object { $text.Contains($_) }

Write-Host "File : $dll"
Write-Host "Size : $($bytes.Length) bytes"
Write-Host "Built: $((Get-Item $dll).LastWriteTime)"
Write-Host "SHA256: $((Get-FileHash $dll -Algorithm SHA256).Hash)"
Write-Host ""

if ($found) {
    Write-Host "FAIL - this is a DEV build. Found: $($found -join ', ')" -ForegroundColor Red
    Write-Host "Do NOT upload. Rebuild with: dotnet build -c Release" -ForegroundColor Red
    exit 1
}

Write-Host "PASS - public build. No recorder, no /rbt record, no /rbt dump." -ForegroundColor Green
Write-Host "Safe to attach to a GitHub Release."
exit 0

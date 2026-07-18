<#
.SYNOPSIS
  Full pre-flight for an ErenshorBossTimers build.

.DESCRIPTION
  Runs every check that catches a failure which compiles fine and only shows up
  in-game, plus the one that matters for publishing: is this the PUBLIC build or
  the dev build?

    1. shipping-safety  Release must contain no fight recorder and no
                        /rbt record or /rbt dump. The dev build writes every
                        chat line your group types to disk; shipping that by
                        accident is the failure this script exists to prevent.
    2. discoverability  Lunaris Cecil-scans each DLL before loading it. If that
                        throws, the plugin is SILENTLY skipped.
    3. vector binding   ImGui vectors must bind to System.Numerics.Vectors or
                        every ImGui call throws MissingMethodException.
    4. vault id         [assembly: AssemblyMetadata("LunarisPluginId", ...)]
                        ties the DLL to its Erenshor Vault page.
    5. auras.json       the live config still parses.

  Checks 2-4 need the .NET SDK and Lunaris.Boot.dll; if either is missing they
  are skipped with a warning rather than failing the run, so the script still
  works as a shipping-safety gate on a machine without the SDK.

.PARAMETER Configuration
  Release (default) = the public build, the one you upload.
  Debug             = the dev build; shipping-safety is inverted (the recorder
                      is EXPECTED, and its absence is the error).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Verify-Release.ps1
  powershell -ExecutionPolicy Bypass -File tools\Verify-Release.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$GameRoot = 'E:\SteamLibrary\steamapps\common\Erenshor',

    [string]$VaultSlug = 'rulys-boss-timers-rbt',

    # Skip the Cecil-based checks even if the SDK is present.
    [switch]$SkipScan
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$dll = Join-Path $root "bin\$Configuration\ErenshorBossTimers.dll"
$fail = $false

function Say-Ok   ($m) { Write-Host "  OK    $m" -ForegroundColor Green }
function Say-Fail ($m) { Write-Host "  FAIL  $m" -ForegroundColor Red;    $script:fail = $true }
function Say-Warn ($m) { Write-Host "  WARN  $m" -ForegroundColor Yellow }

Write-Host ""
Write-Host "ErenshorBossTimers - $Configuration pre-flight" -ForegroundColor Cyan
Write-Host ("-" * 60)

# ---------------------------------------------------------------- the binary
if (-not (Test-Path $dll)) {
    Write-Host "  FAIL  not built: $dll" -ForegroundColor Red
    Write-Host "        run: dotnet build -c $Configuration"
    exit 1
}
$dll = (Resolve-Path $dll).Path
$bytes = [System.IO.File]::ReadAllBytes($dll)
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash

Write-Host "file   : $dll"
Write-Host "size   : $($bytes.Length) bytes"
Write-Host "built  : $((Get-Item $dll).LastWriteTime)"
Write-Host "sha256 : $hash"
Write-Host ""

# ------------------------------------------------------- 1. shipping safety
# .NET metadata stores member and type names as UTF-8, so a byte scan for these
# names is a reliable presence test - no reflection needed.
$text = [System.Text.Encoding]::UTF8.GetString($bytes)
$devMarkers = 'FightRecorder', 'Command_Record', 'Command_Dump'
$found = @($devMarkers | Where-Object { $text.Contains($_) })

if ($Configuration -eq 'Release') {
    if ($found.Count -gt 0) {
        Say-Fail "DEV build in bin\Release - found: $($found -join ', ')"
        Write-Host "        Do NOT upload. Rebuild: dotnet build -c Release" -ForegroundColor Red
    } else {
        Say-Ok "public build - no recorder, no /rbt record, no /rbt dump"
    }
} else {
    if ($found.Count -eq $devMarkers.Count) {
        Say-Ok "dev build - recorder present (RBT_DEV defined)"
    } else {
        Say-Fail "Debug build is missing RBT_DEV pieces: $(($devMarkers | Where-Object { $found -notcontains $_ }) -join ', ')"
    }
}

# ------------------------------------------------- 2-4. Cecil-based checks
$scanProj = Join-Path $PSScriptRoot 'PluginScan\PluginScan.csproj'
$cecil = Join-Path $PSScriptRoot 'PluginScan\Mono.Cecil.dll'
$boot = Join-Path $GameRoot 'Lunaris.Boot.dll'

if ($SkipScan) {
    Say-Warn "Cecil checks skipped (-SkipScan)"
} elseif (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Say-Warn "Cecil checks skipped - no dotnet SDK on PATH"
} elseif (-not (Test-Path $boot)) {
    Say-Warn "Cecil checks skipped - Lunaris.Boot.dll not found under $GameRoot"
} else {
    # Extract the exact Mono.Cecil that Lunaris itself uses, from its embedded
    # resources. Using Lunaris's own copy means this scan reproduces its real
    # pre-load behaviour instead of approximating it with a NuGet build.
    if (-not (Test-Path $cecil)) {
        try {
            $asm = [System.Reflection.Assembly]::LoadFrom($boot)
            $s = $asm.GetManifestResourceStream('Lunaris.Mono.Cecil.dll')
            $fs = [System.IO.File]::Create($cecil)
            $s.CopyTo($fs); $fs.Close(); $s.Close()
            Say-Ok "extracted Mono.Cecil from Lunaris.Boot.dll"
        } catch {
            Say-Warn "could not extract Mono.Cecil: $($_.Exception.Message)"
        }
    }

    if (Test-Path $cecil) {
        $build = & dotnet build $scanProj -c Release -v q --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            Say-Warn "PluginScan build failed; Cecil checks skipped"
            $build | Select-Object -Last 5 | ForEach-Object { Write-Host "        $_" }
        } else {
            $exe = Join-Path $PSScriptRoot 'PluginScan\bin\Release\net8.0\PluginScan.dll'
            $out = & dotnet $exe $dll $GameRoot $VaultSlug 2>&1
            $out | ForEach-Object { Write-Host "  $_" }
            if ($LASTEXITCODE -ne 0) { $script:fail = $true }
        }
    }
}

# ------------------------------------------------------------ 5. auras.json
$auras = Join-Path $GameRoot 'plugins\auras.json'
if (Test-Path $auras) {
    try {
        $n = (Get-Content $auras -Raw | ConvertFrom-Json).Count
        Say-Ok "auras.json parses - $n auras"
    } catch {
        Say-Fail "auras.json does not parse: $($_.Exception.Message)"
    }
} else {
    Say-Warn "auras.json not found (fine on a clean install - it is written on first run)"
}

# ----------------------------------------------------------------- verdict
Write-Host ""
if ($fail) {
    Write-Host "PRE-FLIGHT FAILED - do not publish this build." -ForegroundColor Red
    exit 1
}

Write-Host "PRE-FLIGHT PASSED" -ForegroundColor Green
if ($Configuration -eq 'Release') {
    Write-Host ""
    Write-Host "Upload this file:" -ForegroundColor Cyan
    Write-Host "  $dll"
    Write-Host "  sha256 $hash"
}
exit 0

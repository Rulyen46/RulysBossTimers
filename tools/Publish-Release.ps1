<#
.SYNOPSIS
  Version-bump, build, verify, sync, commit, tag and push a release of RBT.

.DESCRIPTION
  Automates every mechanical step of shipping an update, in the one order that
  keeps the three version numbers in agreement - the [LunarisPlugin] attribute
  (what the in-game card shows), the git tag, and the release title. Those
  drifting apart is the failure this script prevents.

  Order matters and is deliberate:
    1. bump the version in Plugin.cs
    2. build Release  (so the DLL carries the NEW version)
    3. run the pre-flight  -> ABORT and roll the bump back if it fails
    4. mirror source into the repo, commit, tag, push
    5. create the GitHub release + upload the DLL

  Step 5 needs the `gh` CLI. Without it the script stops after pushing the tag
  and prints the exact manual steps, because a release whose asset was uploaded
  by hand still has to be verified by hand.

  Nothing is pushed until you confirm. Use -DryRun to see the plan and touch
  nothing at all.

.PARAMETER Bump
  patch (default) 1.0.0 -> 1.0.1 | minor -> 1.1.0 | major -> 2.0.0

.PARAMETER Version
  Explicit version, overrides -Bump. e.g. -Version 1.2.0

.PARAMETER Notes
  Release notes body. Defaults to a single line naming the version.

.PARAMETER DryRun
  Print the plan and exit. No files written, nothing built, nothing pushed.

.PARAMETER Force
  Skip the confirmation prompt. For unattended use only.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -DryRun
  powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -Bump patch
  powershell -ExecutionPolicy Bypass -File tools\Publish-Release.ps1 -Version 1.1.0 -Notes "Adds Xjeris timers."
#>
[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Notes,

    [string]$RepoDir = 'G:\RulysBossTimers',

    [switch]$DryRun,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$proj = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pluginCs = Join-Path $proj 'Plugin.cs'

function Step ($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Ok   ($m) { Write-Host "   $m" -ForegroundColor Green }
function Bad  ($m) { Write-Host "   $m" -ForegroundColor Red }

# The exact source set that belongs in the public repo. Everything else - bin,
# obj, lib, .vs, fights, docs, BOSS_REVIEW.md - is deliberately excluded; see
# .gitignore. Listing paths explicitly beats excluding, because a new build
# artefact can never silently ride along.
$sourceSet = @(
    '.gitignore', 'README.md', 'ErenshorBossTimers.csproj', 'Plugin.cs', 'Commands.cs',
    'Config\BossTimersConfig.cs',
    'Core\AuraDefinition.cs', 'Core\BossTimersSettings.cs', 'Core\FightRecorder.cs',
    'Core\HpWatcher.cs', 'Core\Log.cs', 'Core\TimerManager.cs', 'Core\TriggerEngine.cs',
    'Core\Zones.cs',
    'Patches\ChatLogPatch.cs',
    'UI\OverlayRenderer.cs',
    'tools\Analyze-Fights.ps1', 'tools\Verify-Release.ps1', 'tools\Publish-Release.ps1',
    'tools\PluginScan\Program.cs', 'tools\PluginScan\PluginScan.csproj'
)

# ------------------------------------------------------------ current version
$attrRe = '(\[LunarisPlugin\("RBT",\s*")(\d+\.\d+\.\d+)(")'
$src = Get-Content $pluginCs -Raw
$m = [regex]::Match($src, $attrRe)
if (-not $m.Success) {
    Bad "Could not find the [LunarisPlugin(`"RBT`", `"X.Y.Z`"...)] version in Plugin.cs."
    Bad "If the attribute was renamed, update `$attrRe in this script."
    exit 1
}
$current = $m.Groups[2].Value

if ($Version) {
    $next = $Version
} else {
    $p = $current.Split('.')
    $maj = [int]$p[0]; $min = [int]$p[1]; $pat = [int]$p[2]
    if     ($Bump -eq 'major') { $maj++; $min = 0; $pat = 0 }
    elseif ($Bump -eq 'minor') { $min++; $pat = 0 }
    else                       { $pat++ }
    $next = "$maj.$min.$pat"
}
$tag = "v$next"
if (-not $Notes) { $Notes = "Ruly's Boss Timers $next" }

$hasGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)

Write-Host ""
Write-Host "Publish plan" -ForegroundColor Cyan
Write-Host ("-" * 60)
Write-Host "  version   : $current  ->  $next"
Write-Host "  tag       : $tag"
Write-Host "  project   : $proj"
Write-Host "  repo      : $RepoDir"
if ($hasGh) { Write-Host "  gh CLI    : found - release will be created and the DLL uploaded" }
else        { Write-Host "  gh CLI    : NOT found - stops after the tag push; manual steps printed" -ForegroundColor Yellow }

if ($DryRun) { Write-Host "`nDry run - nothing changed." -ForegroundColor Yellow; exit 0 }

# ------------------------------------------------------------- sanity checks
if (-not (Test-Path $RepoDir)) { Bad "Repo not found: $RepoDir"; exit 1 }
$existing = & git -C $RepoDir tag --list $tag
if ($existing) { Bad "Tag $tag already exists. Bump again or delete it first."; exit 1 }

if (-not $Force) {
    Write-Host ""
    $answer = Read-Host "Proceed? This commits, tags and pushes to the public repo. (y/N)"
    if ($answer -ne 'y' -and $answer -ne 'Y') { Write-Host "Aborted."; exit 0 }
}

# --------------------------------------------------------------- 1. the bump
Step "1/5  Bumping version in Plugin.cs"
$backup = $src
[System.IO.File]::WriteAllText($pluginCs, [regex]::Replace($src, $attrRe, "`${1}$next`${3}"))
Ok "$current -> $next"

function Undo-Bump {
    [System.IO.File]::WriteAllText($pluginCs, $backup)
    Bad "Rolled Plugin.cs back to $current. Nothing was committed or pushed."
}

# ------------------------------------------------------------- 2. build, 3. verify
Step "2/5  Building Release"
& dotnet build (Join-Path $proj 'ErenshorBossTimers.csproj') -c Release -v quiet --nologo
if ($LASTEXITCODE -ne 0) { Undo-Bump; exit 1 }
Ok "build succeeded"

Step "3/5  Pre-flight"
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Verify-Release.ps1')
if ($LASTEXITCODE -ne 0) { Undo-Bump; exit 1 }

$dll = Join-Path $proj 'bin\Release\ErenshorBossTimers.dll'
$hash = (Get-FileHash $dll -Algorithm SHA256).Hash

# ------------------------------------------------------- 4. sync, commit, tag
Step "4/5  Syncing source and pushing"
foreach ($rel in $sourceSet) {
    $from = Join-Path $proj $rel
    if (-not (Test-Path $from)) { Write-Host "   skip (absent): $rel" -ForegroundColor DarkGray; continue }
    $to = Join-Path $RepoDir $rel
    $dir = Split-Path $to -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Copy-Item $from $to -Force
}
Ok "$($sourceSet.Count) source paths mirrored"

# Refuse to commit a binary even if .gitignore is ever edited badly.
& git -C $RepoDir add -A
$staged = & git -C $RepoDir diff --cached --name-only
$binaries = @($staged | Where-Object { $_ -match '\.(dll|pdb|exe|jsonl)$' })
if ($binaries.Count -gt 0) {
    Bad "Refusing to commit binaries: $($binaries -join ', ')"
    & git -C $RepoDir reset | Out-Null
    Undo-Bump
    exit 1
}

if (-not $staged) {
    Write-Host "   no source changes to commit (tagging the existing HEAD)" -ForegroundColor DarkGray
} else {
    & git -C $RepoDir commit -m "Release $next" | Out-Null
    Ok "committed: Release $next"
}

& git -C $RepoDir tag -a $tag -m "Ruly's Boss Timers $next"
& git -C $RepoDir push
if ($LASTEXITCODE -ne 0) { Bad "git push failed - tag created locally but not pushed."; exit 1 }
& git -C $RepoDir push origin $tag
if ($LASTEXITCODE -ne 0) { Bad "tag push failed."; exit 1 }
Ok "pushed main and $tag"

# --------------------------------------------------------------- 5. release
Step "5/5  GitHub release"
if ($hasGh) {
    & gh release create $tag $dll --title "Ruly's Boss Timers $next" --notes $Notes --repo Rulyen46/RulysBossTimers
    if ($LASTEXITCODE -ne 0) { Bad "gh release create failed - tag is pushed, create the release manually."; exit 1 }
    Ok "release $tag published with the DLL attached"
} else {
    Write-Host "   gh CLI not installed - finish these two steps by hand:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "   a) https://github.com/Rulyen46/RulysBossTimers/releases/new?tag=$tag"
    Write-Host "      title : Ruly's Boss Timers $next"
    Write-Host "      attach: $dll"
    Write-Host ""
    Write-Host "   b) verify what the public gets - this hash must match:"
    Write-Host "      $hash"
    Write-Host "      curl -L -o `"`$env:TEMP\rbt.dll`" https://github.com/Rulyen46/RulysBossTimers/releases/download/$tag/ErenshorBossTimers.dll"
    Write-Host "      (Get-FileHash `"`$env:TEMP\rbt.dll`" -Algorithm SHA256).Hash"
}

Write-Host ""
Write-Host "Done - $next tagged as $tag" -ForegroundColor Green
Write-Host "  upload to Erenshor Vault: $dll"
Write-Host "  sha256 $hash"

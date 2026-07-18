<#
.SYNOPSIS
  Mines plugins/fights/*.jsonl recorded by ErenshorBossTimers and reports
  candidate boss mechanics to turn into auras.

.DESCRIPTION
  Mechanics are RARE; damage is COMMON. To exploit that, each line is reduced to
  a template: numbers become '#' and actor names become '<name>'. Without the
  name pass, "Hobbler feels better" and "Brixa feels better" look like two
  unique rare lines and bury the real mechanics in noise. Names are detected
  from the data itself (capitalised words that recur), so no hardcoded roster.

  Output is tiered rather than filtered. Tier 1 is the LogTypes where mechanics
  have actually been observed; Tier 2 is everything else that is rare, in case a
  mechanic shows up in a category we have not seen one in yet. The recorder
  keeps every line on disk regardless, so nothing here is destructive.

  For each candidate it reports the health percentages the line fired at. A
  tight cluster (like Tojokom's summon at ~50%) means the mechanic keys off
  health, so it can have an HP-watch aura that warns BEFORE it fires - which
  log text alone can never do.

.EXAMPLE
  .\Analyze-Fights.ps1
  .\Analyze-Fights.ps1 -Boss Tojokom -RareMax 5 -Top 15

.EXAMPLE
  # Measure a telegraph -> impact gap to get a real DurationSeconds.
  .\Analyze-Fights.ps1 -From "executes a whirlwind attack" -To "whirling blades"

.EXAMPLE
  # How often does a given ability repeat?
  .\Analyze-Fights.ps1 -Pattern "whirlwind"
#>
[CmdletBinding()]
param(
    [string]$Path = "E:\SteamLibrary\steamapps\common\Erenshor\plugins\fights",
    # Templates occurring more than this many times are treated as spam.
    [int]$RareMax = 8,
    # Max candidates to print in the tier-2 list.
    [int]$Top = 25,
    # Optional: only consider events whose target name matches this.
    [string]$Boss,

    # -- Timing modes -------------------------------------------------------
    # Measure the gap between a telegraph and its effect, e.g.
    #   -From "executes a whirlwind attack" -To "whirling blades"
    # Prints every observed gap plus min/median/max: that median IS the
    # DurationSeconds for the aura. This replaces eyeballing the bar in game.
    [string]$From,
    [string]$To,
    # How many seconds after -From to look for -To.
    [double]$Window = 30,
    # Restrict the recurrence-interval report to lines matching this regex.
    [string]$Pattern
)

# LogTypes where real mechanics have actually been observed so far.
$MechanicTypes = @('BattleMechanicText','ZoneEmotes','NPCEmotes','SpellEmotes')

if (-not (Test-Path $Path)) { Write-Error "No fights folder at $Path. Fight a boss first."; return }
$files = Get-ChildItem -Path $Path -Filter *.jsonl -ErrorAction SilentlyContinue
if (-not $files) { Write-Error "No .jsonl files in $Path yet."; return }

$events = foreach ($f in $files) {
    foreach ($line in Get-Content $f.FullName) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $o = $line | ConvertFrom-Json } catch { continue }
        if ($null -eq $o.text) { continue }   # skips the session header line
        $o | Add-Member -NotePropertyName file -NotePropertyValue $f.Name -Force
        $o
    }
}
if ($Boss)   { $events = $events | Where-Object { $_.tgt -like "*$Boss*" } }
if (-not $events) { Write-Error "No events found$(if($Boss){" for boss '$Boss'"})."; return }

# --- Learn the actor names from the data ------------------------------------
# Any capitalised word recurring often is a player/pet/mob name. Collapsing them
# is what separates "<name> feels better" (spam, 40x) from a real mechanic.
$wordCount = @{}
foreach ($e in $events) {
    foreach ($m in [regex]::Matches($e.text, '\b[A-Z][a-z]{2,}\b')) {
        $w = $m.Value
        $wordCount[$w] = 1 + ($wordCount[$w] | ForEach-Object { $_ })
    }
}
$names = @($wordCount.Keys | Where-Object { $wordCount[$_] -ge 5 })
foreach ($e in $events) { if ($e.tgt -and $names -notcontains $e.tgt) { $names += $e.tgt } }
$nameRegex = if ($names.Count) {
    [regex]('\b(' + (($names | Sort-Object Length -Descending | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b')
} else { $null }

function Get-Template([string]$t) {
    $s = $t -replace '\d+', '#'
    if ($nameRegex) { $s = $nameRegex.Replace($s, '<name>') }
    $s.Trim()
}

function Show-Candidate($g) {
    $ev    = $g.Group
    $types = ($ev | Select-Object -ExpandProperty type -Unique) -join ','
    $srcs  = ($ev | Where-Object src | Select-Object -ExpandProperty src -Unique) -join ','

    # Prefer srcHp (the mob named in the line) over hp (whoever you happened to
    # be targeting). Only srcHp is safe to infer a threshold from.
    $hps    = @($ev | Where-Object { $null -ne $_.srcHp } | Select-Object -ExpandProperty srcHp)
    $hpKind = 'srcHp (named mob)'
    if (-not $hps.Count) {
        $hps    = @($ev | Where-Object { $null -ne $_.hp } | Select-Object -ExpandProperty hp)
        $hpKind = 'hp (your target - may be the wrong mob)'
    }

    Write-Host ("[{0}] x{1}" -f $types, $g.Count) -ForegroundColor Yellow
    Write-Host ("   {0}" -f $ev[0].text)
    if ($srcs) { Write-Host ("   named mob(s): {0}" -f $srcs) -ForegroundColor DarkGray }

    if ($hps.Count) {
        $avg    = ($hps | Measure-Object -Average).Average
        $spread = ($hps | Measure-Object -Maximum).Maximum - ($hps | Measure-Object -Minimum).Minimum
        $list   = ($hps | ForEach-Object { "{0:N1}" -f $_ }) -join ', '
        $files  = @($ev | Select-Object -ExpandProperty file -Unique)

        # A tight HP cluster is only meaningful ACROSS SEPARATE FIGHTS. Within
        # one fight, anything that fires in a burst clusters trivially (every
        # caster interrupted at once looks "HP-triggered" but is not). Requiring
        # 2+ files is what separates a real threshold from a coincidence.
        if ($hps.Count -ge 2 -and $spread -le 5 -and $files.Count -ge 2) {
            Write-Host ("   HP% at fire [{0}]: {1}  (avg {2:N1}, spread {3:N1})" -f $hpKind, $list, $avg, $spread) -ForegroundColor Green
            Write-Host ("   -> HP-TRIGGERED across {0} fights: HP-watch aura at ~{1:N0}%" -f $files.Count, $avg) -ForegroundColor Green
        } elseif ($hps.Count -ge 2 -and $spread -le 5) {
            Write-Host ("   HP% at fire [{0}]: {1}  (avg {2:N1}, spread {3:N1})" -f $hpKind, $list, $avg, $spread) -ForegroundColor Yellow
            Write-Host ("   -> clustered, but only in ONE fight. Could be a burst, not a threshold. Fight it again." ) -ForegroundColor Yellow
        } else {
            Write-Host ("   HP% at fire [{0}]: {1}" -f $hpKind, $list) -ForegroundColor DarkGray
        }
    }

    # Timing context: measure a real DurationSeconds instead of guessing one.
    $first = $ev[0]
    $events | Where-Object { $_.file -eq $first.file -and $_.t -gt $first.t -and $_.t -le ($first.t + 12) } |
        Sort-Object t | Select-Object -First 3 |
        ForEach-Object { Write-Host ("      +{0,5:N2}s [{1}] {2}" -f ($_.t - $first.t), $_.type, $_.text) -ForegroundColor DarkGray }
    Write-Host ""
}

function Get-Stats($values) {
    $sorted = @($values | Sort-Object)
    $n = $sorted.Count
    $median = if ($n % 2) { $sorted[[int](($n - 1) / 2)] }
              else { ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2 }
    [pscustomobject]@{
        Count  = $n
        Min    = $sorted[0]
        Max    = $sorted[-1]
        Median = [math]::Round($median, 2)
        Mean   = [math]::Round(($values | Measure-Object -Average).Average, 2)
    }
}

# --- -From/-To mode: measure a telegraph -> effect gap -----------------------
if ($From) {
    if (-not $To) { Write-Error "-From requires -To (the effect to measure to)."; return }

    Write-Host "`n=== LEAD TIME: '$From' -> '$To' (window ${Window}s) ===" -ForegroundColor Cyan
    $starts = @($events | Where-Object { $_.text -match $From } | Sort-Object file, t)
    if (-not $starts) { Write-Error "No lines matched -From '$From'."; return }

    $gaps = @()
    foreach ($s in $starts) {
        $hit = $events |
            Where-Object { $_.file -eq $s.file -and $_.t -gt $s.t -and $_.t -le ($s.t + $Window) -and $_.text -match $To } |
            Sort-Object t | Select-Object -First 1
        if (-not $hit) {
            Write-Host ("  t={0,8:N2}  {1}" -f $s.t, $s.text)
            Write-Host ("             -> no '$To' within ${Window}s") -ForegroundColor DarkYellow
            continue
        }
        $d = [math]::Round($hit.t - $s.t, 2)
        $gaps += $d
        Write-Host ("  t={0,8:N2}  {1}" -f $s.t, $s.text)
        Write-Host ("             +{0,5:N2}s  {1}" -f $d, $hit.text) -ForegroundColor DarkGray
    }

    if ($gaps.Count) {
        $st = Get-Stats $gaps
        Write-Host ("`n  gaps: {0}" -f (($gaps | ForEach-Object { "{0:N2}s" -f $_ }) -join ', '))
        Write-Host ("  min {0:N2}s | median {1:N2}s | mean {2:N2}s | max {3:N2}s  (n={4})" -f $st.Min, $st.Median, $st.Mean, $st.Max, $st.Count) -ForegroundColor Green
        Write-Host ("`n  -> Use DurationSeconds: {0}   (median; use max {1} to be safe)" -f $st.Median, $st.Max) -ForegroundColor Green
        Write-Host "     Measured from real fight data, not guessed."
    } else {
        Write-Host "`n  No pairs found - widen -Window or loosen -To." -ForegroundColor Yellow
    }
    return
}

Write-Host "`n=== SESSIONS ===" -ForegroundColor Cyan
"{0} events across {1} file(s); learned {2} actor names" -f $events.Count, $files.Count, $names.Count

Write-Host "`n=== KILLS (encounter boundaries) ===" -ForegroundColor Cyan
$events | Where-Object { $_.text -match 'has been slain' } |
    ForEach-Object { "  t={0,9:N2}  {1}" -f $_.t, $_.text }

Write-Host "`n=== LOG TYPE DISTRIBUTION ===" -ForegroundColor Cyan
$events | Group-Object type | Sort-Object Count -Descending |
    ForEach-Object { "  {0,6}  {1}" -f $_.Count, $_.Name }

# -Pattern means "I already know the line I care about" - skip discovery and go
# straight to the timers, or the answer drowns in the full report.
if (-not $Pattern) {
    Write-Host "`n=== TIER 1: MECHANIC LOG TYPES ===" -ForegroundColor Cyan
    Write-Host "Every line from $($MechanicTypes -join ', ') - this is where mechanics have been found.`n"
    $tier1 = $events | Where-Object { $MechanicTypes -contains $_.type } | Group-Object { Get-Template $_.text } | Sort-Object Count
    if ($tier1) { $tier1 | ForEach-Object { Show-Candidate $_ } } else { Write-Host "  (none seen yet)`n" }

    Write-Host "=== TIER 2: OTHER RARE LINES (<= $RareMax occurrences, top $Top) ===" -ForegroundColor Cyan
    Write-Host "In case a mechanic hides in a category we have not seen one in before.`n"
    $events | Where-Object { $MechanicTypes -notcontains $_.type } |
        Group-Object { Get-Template $_.text } |
        Where-Object { $_.Count -le $RareMax } |
        Sort-Object Count | Select-Object -First $Top |
        ForEach-Object { Show-Candidate $_ }
}

Write-Host "=== ABILITY TIMERS (recurrence intervals) ===" -ForegroundColor Cyan
Write-Host "How long between repeats of the same line. Gaps are measured WITHIN a single"
Write-Host "fight - never across files, since t resets each game session.`n"

$timerPool = if ($Pattern) { $events | Where-Object { $_.text -match $Pattern } }
             else { $events | Where-Object { $MechanicTypes -contains $_.type } }

$any = $false
foreach ($g in ($timerPool | Group-Object { Get-Template $_.text } | Sort-Object Count -Descending)) {
    $deltas = @()
    foreach ($fg in ($g.Group | Group-Object file)) {
        $ts = @($fg.Group | Sort-Object t | Select-Object -ExpandProperty t)
        for ($i = 1; $i -lt $ts.Count; $i++) { $deltas += [math]::Round($ts[$i] - $ts[$i - 1], 2) }
    }
    if (-not $deltas.Count) { continue }
    $any = $true

    $st = Get-Stats $deltas
    Write-Host ("[{0}] x{1}" -f (($g.Group | Select-Object -ExpandProperty type -Unique) -join ','), $g.Count) -ForegroundColor Yellow
    Write-Host ("   {0}" -f $g.Group[0].text)
    Write-Host ("   intervals: {0}" -f (($deltas | ForEach-Object { "{0:N1}s" -f $_ }) -join ', ')) -ForegroundColor DarkGray

    # A steady interval means a real cooldown worth a bar. "Steady" is relative:
    # +/-20% of the median, so a 30s cooldown tolerates ~6s of jitter.
    $tol = [math]::Max(1.0, $st.Median * 0.2)
    if ($st.Count -ge 2 -and ($st.Max - $st.Min) -le ($tol * 2)) {
        Write-Host ("   -> RECURS every ~{0:N1}s (min {1:N1} / max {2:N1}, n={3}) - looks like a cooldown" -f $st.Median, $st.Min, $st.Max, $st.Count) -ForegroundColor Green
    } else {
        Write-Host ("   -> irregular: median {0:N1}s, range {1:N1}-{2:N1}s (n={3}) - probably reactive, not on a timer" -f $st.Median, $st.Min, $st.Max, $st.Count) -ForegroundColor DarkGray
    }
    Write-Host ""
}
if (-not $any) { Write-Host "  (nothing repeated yet - needs a line that fires 2+ times in one fight)`n" }

Write-Host "=== NEXT STEP ===" -ForegroundColor Cyan
@"
Pick a candidate above, add it to plugins/auras.json, then run /bosstimers reload
(no rebuild needed). Use the template text, minus the <name>/# placeholders, as
the MatchPattern - or keep it generic to catch every mob using that mechanic.

  Log aura (fires on text):
    { "Name":"...", "MatchPattern":"is preparing a POWERFUL ATTACK",
      "DurationSeconds":5.0, "ColorHex":"#D85A30", "Enabled":true }

  Persistent aura (until something clears it):
    { "Name":"Mutt is up", "MatchPattern":"summons a companion",
      "StopPattern":"Mutt has been slain", "DurationSeconds":0.0,
      "ColorHex":"#D85A30", "Enabled":true }

  HP-watch aura (warns BEFORE a threshold mechanic fires):
    { "Name":"Mutt spawn approaching", "WatchNpcName":"Tojokom",
      "HpThresholdPercent":50.0, "WarnWithinPercent":10.0,
      "ColorHex":"#E0B040", "Enabled":true }

Set DurationSeconds from the +Ns timing lines, not from guesswork.
"@

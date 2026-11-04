# Lockstep validation (docs/design/LOCKSTEP_SIM.md phase 3): paced x20 against
# lockstep, same bots, same seed, same game start time, one run after another.
#
# Build Release first:
#   dotnet build -c Release server/FolkIdle.Server/FolkIdle.Server.csproj
#   dotnet build -c Release server/FolkIdle.Bots/FolkIdle.Bots.csproj
# then:
#   .\server\FolkIdle.Bots\night-lockstep-validation.ps1 [-Out bot-runs\night-X] [-Seed 1]
#       [-StartUtc 2026-11-05T07:00:00Z] [-FinishBy 07:30] [-DeepHours 0]
#
# Order: val-x20 (reference), val-ls-scale1, then val-ls-scale4 only if its
# estimated length still ends before -FinishBy (local time). The run STOPS
# after the reference if the reference itself is invalid (a bot that never
# logged in or stopped) - nothing can be compared against it. validation.md
# is written by compare-validation.py, whose exit code 2 means INVALID.
#
# Needs Docker with folk-idle-db and folk-idle-redis. Each run gets a fresh
# database with synchronous_commit off.
param(
    [string]$Out = "",
    [int]$Seed = 1,
    [double]$ValidationHours = 72,
    [double]$DeepHours = 0,
    [string]$StartUtc = "",
    [string]$FinishBy = "07:30",
    [double]$Scale4EstimateMinutes = 110
)
$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$night = if ($Out) { $Out } else { "$repo\bot-runs\night-" + (Get-Date -Format 'yyyyMMdd') }
New-Item -ItemType Directory -Force $night | Out-Null
$mix = 'optimal:5,patientbare:5,eagerkit:5,patient:5'
$expected = 20
$log = "$night\night.log"
# Say writes to the log and the console, never to the pipeline: Run returns a path.
function Say($t) { $line = "$(Get-Date -Format 'HH:mm:ss') $t"; Add-Content -Path $log -Value $line; Write-Host $line }

# Game time zero for EVERY run: 07:00 UTC today unless given - after the
# sleepers' shared 00:30-06:30 core, so all bots start awake.
if (-not $StartUtc) { $StartUtc = (Get-Date).ToUniversalTime().Date.AddHours(7).ToString('yyyy-MM-ddTHH:mm:ssZ') }
$start = [DateTime]::Parse($StartUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]'AdjustToUniversal,AssumeUniversal')
$finish = [DateTime]::ParseExact($FinishBy, 'HH:mm', $null)
if ($finish -lt (Get-Date)) { $finish = $finish.AddDays(1) }

function Run([string]$Tag, [string]$Mode, [int]$Scale, [double]$Hours) {
    $out = "$night\$Tag"
    New-Item -ItemType Directory -Force $out | Out-Null
    Get-Process FolkIdle.Server, FolkIdle.Bots -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep 2
    $db = "folkidle_night_$Tag".ToLower() -replace '[^a-z0-9_]', '_'
    docker exec folk-idle-db psql -q -U postgres -d postgres -c "DROP DATABASE IF EXISTS $db WITH (FORCE);" 2>$null | Out-Null
    docker exec folk-idle-db psql -q -U postgres -d postgres -c "CREATE DATABASE $db;" | Out-Null
    docker exec folk-idle-db psql -q -U postgres -d postgres -c "ALTER DATABASE $db SET synchronous_commit = off;" | Out-Null
    $exe = "$repo\server\FolkIdle.Server\bin\Release\net8.0\FolkIdle.Server.exe"
    $env:FOLKIDLE_DB_CONN = "Host=localhost;Database=$db;Username=postgres;Password=postgres"
    & $exe --migrate *> "$out\migrate.log"
    $env:FOLKIDLE_DEV_TOOLS = '1'; $env:FOLKIDLE_BOSS_MINIGAME = 'wheel'; $env:FOLKIDLE_OFFLINE_SIM = '1'
    $env:FOLKIDLE_SIM_START_UTC = $start.ToString('o')
    Remove-Item Env:FOLKIDLE_SIM_LOCKSTEP, Env:FOLKIDLE_TICK_SPEED, Env:FOLKIDLE_SIM_CRON_SCALE -ErrorAction SilentlyContinue
    $speed = 1
    if ($Mode -eq 'lockstep') {
        $env:FOLKIDLE_SIM_LOCKSTEP = '1'; $env:FOLKIDLE_SIM_CRON_SCALE = "$Scale"
        $botArgs = @('--lockstep')
    } else {
        $speed = [int]$Mode
        $env:FOLKIDLE_TICK_SPEED = $Mode
        $botArgs = @('--speed', $Mode)
    }
    $serverStarted = Get-Date
    $srv = Start-Process -FilePath $exe -WorkingDirectory "$repo\server\FolkIdle.Server" -RedirectStandardOutput "$out\server.log" -RedirectStandardError "$out\server.err" -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 90; $i++) { try { if ((Invoke-WebRequest -UseBasicParsing http://localhost:8080/gamedata -TimeoutSec 3).StatusCode -eq 200) { break } } catch {}; Start-Sleep 1 }
    if ($Mode -ne 'lockstep') {
        # The paced server's clock started with the process; the bots' clock
        # starts now. Shift the bots by the game time already gone so both
        # read the same hour of the day.
        $ahead = ((Get-Date) - $serverStarted).TotalSeconds * $speed
        $botArgs += @('--start-utc', $start.AddSeconds($ahead).ToString('o'))
    }
    Say "start $Tag ($Mode, scale $Scale, $Hours game h, game start $($start.ToString('u')), db $db)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Set-Location $repo
    & "$repo\server\FolkIdle.Bots\bin\Release\net8.0\FolkIdle.Bots.exe" @botArgs --mix $mix --hours $Hours --out "$out\bots" --seed $Seed *> "$out\bots.log"
    try { (Invoke-WebRequest -UseBasicParsing http://localhost:8080/metrics -TimeoutSec 5).Content | Out-File "$out\metrics.txt" -Encoding ascii } catch {}
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Say "end $Tag after $([math]::Round($sw.Elapsed.TotalMinutes,1)) wall min"
    return "$out\bots"
}

# A run is usable only if every bot logged in and none stopped.
function Healthy([string]$BotsDir) {
    $path = "$BotsDir\run.json"
    if (-not (Test-Path $path)) { Say "INVALID: $path missing"; return $false }
    $info = Get-Content $path -Raw | ConvertFrom-Json
    if ($info.BotsStarted -ne $expected -or $info.BotsStopped -ne 0) {
        Say "INVALID: $BotsDir started $($info.BotsStarted) of $expected bots, $($info.BotsStopped) stopped"
        return $false
    }
    return $true
}

# Not "Compare": that name is the built-in alias of Compare-Object, and an alias wins over a function.
function Write-Validation([string[]]$Dirs) {
    python -I "$PSScriptRoot\compare-validation.py" @Dirs *> "$night\validation.md"
    Say "validation written: exit $LASTEXITCODE ($(if ($LASTEXITCODE -eq 0) { 'VALID' } else { 'INVALID' }))"
}

Say "night start (game start $($start.ToString('u')), finish by $($finish.ToString('HH:mm')))"
$ref = Run 'val-x20' '20' 1 $ValidationHours
if (-not (Healthy $ref)) {
    Say "the reference is not usable; stopping - lockstep has nothing to be compared against"
    "# Lockstep validation`n`n**Verdict: INVALID.** The reference run $ref did not start all $expected bots or lost some (see night.log); nothing was compared." | Out-File "$night\validation.md" -Encoding utf8
    exit 2
}
$dirs = @($ref, (Run 'val-ls-scale1' 'lockstep' 1 $ValidationHours))
if ((Get-Date).AddMinutes($Scale4EstimateMinutes) -lt $finish) {
    $dirs += Run 'val-ls-scale4' 'lockstep' 4 $ValidationHours
} else {
    Say "val-ls-scale4 skipped: ~$Scale4EstimateMinutes min would end after $($finish.ToString('HH:mm'))"
}
Write-Validation $dirs
if ($DeepHours -gt 0) { Run "deep-$($DeepHours)h" 'lockstep' 4 $DeepHours | Out-Null }
Say "night done"

# Lockstep validation (docs/design/LOCKSTEP_SIM.md phase 3) + one long run.
# First run: night 2026-11-04, from main 4bb7c070 (#313-#316).
#
# Build Release first (dotnet build -c Release server/FolkIdle.Bots/FolkIdle.Bots.csproj),
# then: .\server\FolkIdle.Bots\night-lockstep-validation.ps1 [-Out bot-runs\night-X] [-Seed 1]
# Needs Docker with folk-idle-db and folk-idle-redis. Each run gets a fresh
# database with synchronous_commit off.
param([string]$Out = "", [int]$Seed = 1, [double]$ValidationHours = 72, [double]$DeepHours = 240)
$ErrorActionPreference = 'Continue'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$night = if ($Out) { $Out } else { "$repo\bot-runs\night-" + (Get-Date -Format 'yyyyMMdd') }
New-Item -ItemType Directory -Force $night | Out-Null
$mix = 'optimal:5,patientbare:5,eagerkit:5,patient:5'
$log = "$night\night.log"
function Say($t) { "$(Get-Date -Format 'HH:mm:ss') $t" | Tee-Object -FilePath $log -Append }

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
    Remove-Item Env:FOLKIDLE_SIM_LOCKSTEP, Env:FOLKIDLE_TICK_SPEED, Env:FOLKIDLE_SIM_CRON_SCALE -ErrorAction SilentlyContinue
    if ($Mode -eq 'lockstep') {
        $env:FOLKIDLE_SIM_LOCKSTEP = '1'; $env:FOLKIDLE_SIM_CRON_SCALE = "$Scale"
        $botArgs = @('--lockstep')
    } else {
        $env:FOLKIDLE_TICK_SPEED = $Mode
        $botArgs = @('--speed', $Mode)
    }
    $srv = Start-Process -FilePath $exe -WorkingDirectory "$repo\server\FolkIdle.Server" -RedirectStandardOutput "$out\server.log" -RedirectStandardError "$out\server.err" -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 90; $i++) { try { if ((Invoke-WebRequest -UseBasicParsing http://localhost:8080/gamedata -TimeoutSec 3).StatusCode -eq 200) { break } } catch {}; Start-Sleep 1 }
    Say "start $Tag ($Mode, scale $Scale, $Hours game h, db $db)"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Set-Location $repo
    & "$repo\server\FolkIdle.Bots\bin\Release\net8.0\FolkIdle.Bots.exe" @botArgs --mix $mix --hours $Hours --out "$out\bots" --seed $Seed *> "$out\bots.log"
    try { (Invoke-WebRequest -UseBasicParsing http://localhost:8080/metrics -TimeoutSec 5).Content | Out-File "$out\metrics.txt" -Encoding ascii } catch {}
    Stop-Process -Id $srv.Id -Force -ErrorAction SilentlyContinue
    Say "end $Tag after $([math]::Round($sw.Elapsed.TotalMinutes,1)) wall min"
}

Say "night start"
# Block 1 - phase 3 validation, same mix and seed: paced x20 is the reference.
Run 'val-x20' '20' 1 $ValidationHours
Run 'val-ls-scale1' 'lockstep' 1 $ValidationHours
Run 'val-ls-scale4' 'lockstep' 4 $ValidationHours
python -I "$PSScriptRoot\compare-validation.py" "$night\val-x20\bots" "$night\val-ls-scale1\bots" "$night\val-ls-scale4\bots" *> "$night\validation.md"
Say "validation written"
# Block 2 - deep game: ten game days, lockstep, scale 4.
Run "deep-$($DeepHours)h" 'lockstep' 4 $DeepHours
Say "night done"

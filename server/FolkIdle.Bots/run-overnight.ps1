# An unattended bot run: a fresh folkidle_bots database, the dev server at a
# tick multiplier, and the bots until a wall-clock end time. Run from the repo
# root; build first (dotnet build server/FolkIdle.Bots/FolkIdle.Bots.csproj),
# because this starts both projects with --no-build.
#
# Usage:
#   .\server\FolkIdle.Bots\run-overnight.ps1                                  # now until 08:55, x200
#   .\server\FolkIdle.Bots\run-overnight.ps1 -StartAt 00:00 -EndAt 08:55      # wait for midnight
#   .\server\FolkIdle.Bots\run-overnight.ps1 -Mix optimal:3,patient:3 -Speed 100
#   .\server\FolkIdle.Bots\run-overnight.ps1 -OfflineSim 0                    # the old offline model
#
# -OfflineSim sets FOLKIDLE_OFFLINE_SIM for the server EXPLICITLY (1 = the
# per-swing simulator production runs since #298, the default). The first
# rebirth experiment inherited nothing and silently ran the old model;
# summary.md now states which model the server actually used.
#
# The rebirth experiment (PLAYTEST_BOTS_REPORT.md section 0.6) is the default
# mix: 3 optimal (eager rebirth) vs 3 patient, plus 2 casual and 2 idle.
# The controlled 2x2 (section 0.6.2) is
#   -Mix optimal:5,patientbare:5,eagerkit:5,patient:5 -Seed 1   (then -Seed 2, ...)
# Needs Docker with the folk-idle-db and folk-idle-redis containers.
param(
    [string]$StartAt = "",
    [string]$EndAt = "08:55",
    [int]$Speed = 200,
    [string]$Mix = "optimal:3,patient:3,casual:2,idle:2",
    [string]$Out = "",
    [ValidateSet(0, 1)][int]$OfflineSim = 1,
    [int]$Seed = 0
)

$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
Set-Location $repo

if ($StartAt) {
    $start = [datetime]::ParseExact($StartAt, "HH:mm", $null)
    if ($start -lt (Get-Date)) { $start = $start.AddDays(1) }
    Write-Host "Waiting until $start"
    Start-Sleep -Seconds ([int]($start - (Get-Date)).TotalSeconds)
}

docker start folk-idle-db folk-idle-redis | Out-Null
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
& "$PSScriptRoot\new-bot-db.ps1" | Out-Null

$server = Start-Job -ScriptBlock {
    param($root, $speed, $offlineSim)
    Set-Location $root
    $env:FOLKIDLE_DB_CONN = 'Host=localhost;Database=folkidle_bots;Username=postgres;Password=postgres'
    $env:FOLKIDLE_DEV_TOOLS = '1'
    $env:FOLKIDLE_TICK_SPEED = "$speed"
    $env:FOLKIDLE_BOSS_MINIGAME = 'wheel'   # as production
    $env:FOLKIDLE_OFFLINE_SIM = "$offlineSim"
    dotnet run --no-build --project server/FolkIdle.Server/FolkIdle.Server.csproj *> "$root\bot-runs\server.log"
} -ArgumentList $repo, $Speed, $OfflineSim
New-Item -ItemType Directory -Force "$repo\bot-runs" | Out-Null
for ($i = 0; $i -lt 120; $i++) {
    try { if ((Invoke-WebRequest -UseBasicParsing http://localhost:8080/gamedata -TimeoutSec 3).StatusCode -eq 200) { break } } catch {}
    Start-Sleep -Seconds 3
}

$end = [datetime]::ParseExact($EndAt, "HH:mm", $null)
if ($end -lt (Get-Date)) { $end = $end.AddDays(1) }
$hours = [math]::Round(($end - (Get-Date)).TotalHours * $Speed, 1).ToString([System.Globalization.CultureInfo]::InvariantCulture)
if (-not $Out) { $Out = "bot-runs\overnight-" + (Get-Date -Format "yyyyMMdd-HHmm") }
Write-Host "Bots: $Mix, $hours game hours at x$Speed, FOLKIDLE_OFFLINE_SIM=$OfflineSim, seed $Seed -> $Out"

dotnet run --no-build --project server/FolkIdle.Bots/FolkIdle.Bots.csproj -- --mix $Mix --hours $hours --speed $Speed --out $Out --seed $Seed
Get-Process FolkIdle.Server -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Job $server -ErrorAction SilentlyContinue
Write-Host "Done: $Out\summary.md"

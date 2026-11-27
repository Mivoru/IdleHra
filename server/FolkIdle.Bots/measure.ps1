# A one-week lockstep measurement with production's flags. See
# docs/design/PLAYER_PATH.md for what it measures and how to read the result.
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [string]$Mix = "optimal:4,casual:4,idle:2",
    [int]$Hours = 168,
    [int]$Seed = 1,
    [string]$Tag = "a",
    [string[]]$BotArgs = @()
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $Repo "bot-runs\measure-$Tag-$(Get-Date -Format yyyyMMdd-HHmm)"
New-Item -ItemType Directory -Force $out | Out-Null
$db = "folkidle_measure_$Tag"
docker exec folk-idle-db psql -q -U postgres -d postgres -c "DROP DATABASE IF EXISTS $db WITH (FORCE);" | Out-Null
docker exec folk-idle-db psql -q -U postgres -d postgres -c "CREATE DATABASE $db;" | Out-Null
docker exec folk-idle-db psql -q -U postgres -d postgres -c "ALTER DATABASE $db SET synchronous_commit = off;" | Out-Null
$serverExe = Join-Path $Repo "server\FolkIdle.Server\bin\Release\net8.0\FolkIdle.Server.exe"
$botExe = Join-Path $Repo "server\FolkIdle.Bots\bin\Release\net8.0\FolkIdle.Bots.exe"
$env:FOLKIDLE_DB_CONN = "Host=localhost;Database=$db;Username=postgres;Password=postgres"
& $serverExe --migrate *> "$out\migrate.log"
if ($LASTEXITCODE -ne 0) { throw "migrate failed" }
# Production's flags (ops/oracle/.env, 2026-11-20) plus lockstep.
$env:FOLKIDLE_DEV_TOOLS = '1'; $env:FOLKIDLE_BOSS_MINIGAME = 'duel'; $env:FOLKIDLE_NAV = 'on'
$env:FOLKIDLE_DELVE_DEEP = 'on'; $env:FOLKIDLE_OFFLINE_SIM = '1'
$env:FOLKIDLE_SIM_LOCKSTEP = '1'; $env:FOLKIDLE_SIM_CRON_SCALE = '1'
$env:FOLKIDLE_SIM_START_UTC = (Get-Date).ToUniversalTime().Date.AddHours(7).ToString('o')
Remove-Item Env:FOLKIDLE_TICK_SPEED -ErrorAction SilentlyContinue
$srv = Start-Process -FilePath $serverExe -WorkingDirectory (Join-Path $Repo "server\FolkIdle.Server") -RedirectStandardOutput "$out\server.log" -RedirectStandardError "$out\server.err" -PassThru -WindowStyle Hidden
$null = $srv.Handle
$ready = $false
for ($i = 0; $i -lt 300; $i++) {
    if ($srv.HasExited) { throw "server exited: $(Get-Content "$out\server.err" -Tail 5)" }
    try { if ((Invoke-WebRequest -UseBasicParsing http://localhost:8080/gamedata -TimeoutSec 3).StatusCode -eq 200) { $ready = $true; break } } catch {}
    Start-Sleep 1
}
if (-not $ready) { throw "server not ready" }
"start $(Get-Date -Format o) mix $Mix hours $Hours seed $Seed db $db" | Out-File "$out\measure.txt"
try {
    & $botExe --lockstep --mix $Mix --hours $Hours --seed $Seed --out $out @BotArgs *> "$out\bots.log"
    "bots exit $LASTEXITCODE at $(Get-Date -Format o)" | Out-File -Append "$out\measure.txt"
} finally {
    if (-not $srv.HasExited) { Stop-Process -Id $srv.Id -Force }
}
Write-Output $out

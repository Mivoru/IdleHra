# Copies the Oracle box's nightly database dumps onto this PC, so a lost
# server disk does not also lose every backup.
#
#     powershell -NoProfile -ExecutionPolicy Bypass -File ops\oracle\pull-backups.ps1
#
# Registered as the Windows scheduled task "FolkIdle backup pull" (daily, and
# at logon, and catching up when the PC was off at the scheduled time) - see
# ops/oracle/README.md, "Backups". The box writes a dump at 03:17 UTC and keeps
# 7; this keeps 30, so the PC holds a month of history the box does not.
#
# Modul: A COPY IS ONLY A BACKUP IF IT IS WHOLE. Each file is copied to a
# .partial name and renamed only once its size matches the box's, so a pull cut
# off by a sleeping laptop or a dropped connection is retried next time rather
# than kept as a truncated dump that looks fine until the day it is needed.
param(
    [string]$Dest = 'D:\FolkIdleBackups',
    [int]$Keep = 30,
    [string]$HostAlias = 'folkidle-server'
)

$ErrorActionPreference = 'Stop'
$dumps = Join-Path $Dest 'dumps'
$log = Join-Path $Dest 'pull.log'
New-Item -ItemType Directory -Force -Path $dumps | Out-Null

function Write-Log([string]$message) {
    $line = "{0:u} {1}" -f (Get-Date).ToUniversalTime(), $message
    Add-Content -Path $log -Value $line -Encoding utf8
    Write-Output $line
}

try {
    # name<TAB>size for every finished dump on the box (never the .partial ones).
    $listing = & ssh -o BatchMode=yes -o ConnectTimeout=20 $HostAlias "cd ~/folkidle-backups && for f in folkidle-*.dump; do [ -e `"`$f`" ] && printf '%s\t%s\n' `"`$f`" `"`$(stat -c %s `"`$f`")`"; done"
    if ($LASTEXITCODE -ne 0) { throw "ssh listing failed with exit code $LASTEXITCODE" }

    $remote = @{}
    foreach ($row in $listing) {
        $parts = $row.Trim() -split "`t"
        if ($parts.Count -eq 2) { $remote[$parts[0]] = [long]$parts[1] }
    }
    if ($remote.Count -eq 0) { throw 'the box reported no dumps at all - is backup-db.sh running?' }

    $copied = 0
    foreach ($name in ($remote.Keys | Sort-Object)) {
        $target = Join-Path $dumps $name
        if ((Test-Path $target) -and ((Get-Item $target).Length -eq $remote[$name])) { continue }

        $partial = "$target.partial"
        & scp -q -o BatchMode=yes "${HostAlias}:folkidle-backups/$name" $partial
        if ($LASTEXITCODE -ne 0) { throw "scp of $name failed with exit code $LASTEXITCODE" }

        $got = (Get-Item $partial).Length
        if ($got -ne $remote[$name]) {
            Remove-Item $partial -Force
            throw "$name arrived as $got bytes, the box has $($remote[$name])"
        }
        Move-Item -Force $partial $target
        $copied++
    }

    # Keep the newest $Keep; the names sort by their UTC stamp.
    $all = Get-ChildItem $dumps -Filter 'folkidle-*.dump' | Sort-Object Name -Descending
    $pruned = 0
    if ($all.Count -gt $Keep) {
        $all | Select-Object -Skip $Keep | ForEach-Object { Remove-Item $_.FullName -Force; $pruned++ }
    }
    Get-ChildItem $dumps -Filter '*.partial' | Remove-Item -Force

    $newest = (Get-ChildItem $dumps -Filter 'folkidle-*.dump' | Sort-Object Name -Descending | Select-Object -First 1).Name
    Write-Log "OK - copied $copied, pruned $pruned, holding $([Math]::Min($all.Count, $Keep)); newest $newest"
    exit 0
}
catch {
    Write-Log "FAILED - $($_.Exception.Message)"
    exit 1
}

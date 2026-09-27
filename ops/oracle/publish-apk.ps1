# Modul: builds the RELEASE APK and publishes it where the login screen's
# "Get the Android app" link points (https://<host>/download/folkidle.apk).
#
# Runs on the owner's PC and nowhere else, because that is where the upload
# key is: client_web/android/keystore.properties (gitignored) names
# %USERPROFILE%\.folkidle\folkidle-upload.jks, with a copy of both in
# D:\FolkIdleBackups\signing. Lose that key and every installed copy has to be
# uninstalled before it can take an update - and Play will refuse the listing.
#
# A new APK is needed only when the NATIVE side changes (a Capacitor plugin,
# the manifest, an icon). Game code reaches installed phones over the air on
# every deploy, so most deploys do not need this script at all.
#
# Usage, from the repo root:   .\ops\oracle\publish-apk.ps1
param(
    [string]$Server = 'https://folkidle.duckdns.org',
    [string]$SshHost = 'folkidle-server'
)

# NOT 'Stop': Windows PowerShell 5.1 turns every stderr line of a native tool
# into an error record, so npm's own "npm notice run ..." ended the script
# before it did anything. Every native call below checks $LASTEXITCODE instead.
$ErrorActionPreference = 'Continue'
$repo = Resolve-Path "$PSScriptRoot\..\.."
$client = Join-Path $repo 'client_web'
$apk = Join-Path $client 'android\app\build\outputs\apk\release\app-release.apk'

if (-not (Test-Path (Join-Path $client 'android\keystore.properties'))) {
    throw 'client_web\android\keystore.properties is missing - restore it from D:\FolkIdleBackups\signing. Without it Gradle builds an UNSIGNED apk.'
}

# Vite inlines the server address. Without this the APK would talk to
# localhost, which on a phone is the phone.
$env:VITE_FOLKIDLE_SERVER = $Server

# Gradle outside Android Studio needs to be told where both of these are. The
# system Java on this PC is 17.0.8, which rejects gradlew.bat's empty
# `-classpath ""` ("-classpath requires class path specification"); Android
# Studio's bundled JDK is what the IDE builds with anyway.
if (-not $env:JAVA_HOME) { $env:JAVA_HOME = 'C:\Program Files\Android\Android Studio\jbr' }
if (-not $env:ANDROID_HOME) { $env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk" }

Push-Location $client
try {
    npm run sync
    if ($LASTEXITCODE -ne 0) { throw 'npm run sync failed' }

    # `cap sync` links whatever is in node_modules, not what package.json
    # lists. A plugin missing from node_modules is silently REMOVED from the
    # native projects and the APK ships without it - found on the first run of
    # this script, when haptics and local notifications vanished. The committed
    # native files are the truth, so any diff here means node_modules is stale.
    git -C $repo diff --quiet -- client_web/android client_web/ios
    if ($LASTEXITCODE -ne 0) {
        git -C $repo diff --stat -- client_web/android client_web/ios
        throw 'cap sync changed the committed native projects - run `npm ci` in client_web and try again.'
    }

    Push-Location android
    try {
        .\gradlew.bat --no-daemon assembleRelease
        if ($LASTEXITCODE -ne 0) { throw 'gradle assembleRelease failed' }
    } finally { Pop-Location }
} finally { Pop-Location }

# Refuse to publish an unsigned or wrongly signed file: the signing block in
# build.gradle fails OPEN by design, so "it built" proves nothing here.
$buildTools = Get-ChildItem "$env:LOCALAPPDATA\Android\Sdk\build-tools" | Sort-Object Name | Select-Object -Last 1
$certs = & (Join-Path $buildTools.FullName 'apksigner.bat') verify --print-certs $apk
if ($LASTEXITCODE -ne 0 -or -not ($certs -match 'CN=FolkIdle')) {
    throw "APK is not signed with the FolkIdle upload key:`n$certs"
}

# Upload under a temporary name and rename, so a download that starts
# mid-upload gets the old file whole rather than half of the new one.
ssh $SshHost 'mkdir -p ~/folkidle/ops/oracle/downloads'
if ($LASTEXITCODE -ne 0) { throw 'upload to the box failed' }
scp $apk "${SshHost}:folkidle/ops/oracle/downloads/folkidle.apk.part"
if ($LASTEXITCODE -ne 0) { throw 'upload to the box failed' }
ssh $SshHost 'mv ~/folkidle/ops/oracle/downloads/folkidle.apk.part ~/folkidle/ops/oracle/downloads/folkidle.apk'
if ($LASTEXITCODE -ne 0) { throw 'upload to the box failed' }

$local = (Get-Item $apk).Length
$head = curl.exe -sI "$Server/download/folkidle.apk"
$remote = ($head | Select-String -Pattern '^Content-Length:\s*(\d+)' | ForEach-Object { [long]$_.Matches[0].Groups[1].Value }) | Select-Object -First 1
if ($remote -ne $local) {
    throw "Published APK did not verify: local $local bytes, server answered $remote.`n$($head -join "`n")"
}
Write-Host "Published $Server/download/folkidle.apk ($local bytes)"

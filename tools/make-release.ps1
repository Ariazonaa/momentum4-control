# Baut die Auslieferungs-Artefakte für MOMENTUM 4 Control:
#   1. setup.exe  – per-User-Installer (Inno Setup, kein Admin)
#   2. portable    – ZIP der self-contained App mit Portable-Marker (Daten neben der Exe)
#
# Die Artefakte landen in %LOCALAPPDATA%\Momentum4Control.build\dist (außerhalb des Repos, X: ist SMB).
# Voraussetzung für den Installer: Inno Setup 6 (ISCC.exe). Fehlt es, wird nur die portable Variante gebaut.
#
#   ./tools/make-release.ps1              # Version aus Directory.Build.props
#   ./tools/make-release.ps1 -Version 1.1.0
param(
    [string]$Version = '',
    [string]$Dist = (Join-Path $env:LOCALAPPDATA 'Momentum4Control.build\dist')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# --- Version bestimmen (Parameter oder Directory.Build.props) ---
if (-not $Version) {
    $props = Get-Content (Join-Path $repo 'Directory.Build.props') -Raw
    if ($props -notmatch '<Version>([^<]+)</Version>') { throw 'Version nicht in Directory.Build.props gefunden.' }
    $Version = $Matches[1].Trim()
}
Write-Host "Version: $Version" -ForegroundColor Cyan

# --- Ordner vorbereiten ---
$stageRoot = Join-Path $env:LOCALAPPDATA 'Momentum4Control.build\release-stage'
$appDir = Join-Path $stageRoot 'app'
$portableDir = Join-Path $stageRoot "Momentum4Control-$Version-portable"
foreach ($d in @($stageRoot, $Dist)) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } ; New-Item -ItemType Directory -Force -Path $d | Out-Null }

# --- 1. App veröffentlichen (self-contained AOT, App + m4ctl) ---
Write-Host 'Veröffentliche App (Native AOT) ...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'publish-app.ps1') -Output $appDir
if ($LASTEXITCODE -ne 0) { throw "publish-app.ps1 fehlgeschlagen ($LASTEXITCODE)." }

# --- 2. Portable Variante (Marker + LIESMICH, dann ZIP) ---
Write-Host 'Baue portable Variante ...' -ForegroundColor Cyan
Copy-Item $appDir $portableDir -Recurse
New-Item -ItemType File -Path (Join-Path $portableDir 'Momentum4Control.portable') -Force | Out-Null
@"
MOMENTUM 4 Control – portable
=============================

Diese Variante braucht keine Installation. Entpacke den Ordner an einen
Ort, an den du schreiben darfst (z. B. einen USB-Stick oder Dokumente),
und starte Momentum4Control.exe.

Einstellungen, Logs und EQ-Presets liegen im Unterordner "Data" neben der
Exe (nicht in %LOCALAPPDATA%) – die Datei "Momentum4Control.portable"
schaltet diesen Modus. Löschst du sie, verhält sich die Kopie wie die
installierte Version und schreibt nach %LOCALAPPDATA%\Momentum4Control.

m4ctl.exe im selben Ordner ist die Kommandozeile (z. B. "m4ctl status").

Inoffiziell, nicht von Sennheiser. Keine Cloud, alles lokal.
"@ | Set-Content -Path (Join-Path $portableDir 'LIESMICH.txt') -Encoding UTF8

$portableZip = Join-Path $Dist "Momentum4Control-$Version-portable.zip"
Compress-Archive -Path $portableDir -DestinationPath $portableZip -CompressionLevel Optimal -Force
Write-Host "  → $portableZip ($('{0:N1}' -f ((Get-Item $portableZip).Length / 1MB)) MB)" -ForegroundColor Green

# --- 3. Installer (Inno Setup), sofern ISCC gefunden ---
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }

if ($iscc) {
    Write-Host 'Baue setup.exe (Inno Setup) ...' -ForegroundColor Cyan
    & $iscc `
        "/DAppVersion=$Version" `
        "/DSourceDir=$appDir" `
        "/DRepoDir=$repo" `
        "/DOutputDir=$Dist" `
        (Join-Path $repo 'installer\Momentum4Control.iss')
    if ($LASTEXITCODE -ne 0) { throw "ISCC fehlgeschlagen ($LASTEXITCODE)." }
    $setup = Join-Path $Dist "Momentum4Control-Setup-$Version.exe"
    Write-Host "  → $setup ($('{0:N1}' -f ((Get-Item $setup).Length / 1MB)) MB)" -ForegroundColor Green
} else {
    Write-Warning 'Inno Setup (ISCC.exe) nicht gefunden – nur die portable Variante wurde gebaut.'
    Write-Warning 'Installieren mit:  winget install JRSoftware.InnoSetup   (oder  choco install innosetup -y)'
}

Write-Host "`nFertig. Artefakte in: $Dist" -ForegroundColor Cyan
Get-ChildItem $Dist | Select-Object Name, @{n='MB';e={'{0:N1}' -f ($_.Length/1MB)}} | Format-Table -AutoSize

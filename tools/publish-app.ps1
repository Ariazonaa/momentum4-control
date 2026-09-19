# Veröffentlicht die App als Native-AOT-Build (Release, x64, self-contained). Standard-Ziel ist
# %LOCALAPPDATA%\Momentum4Control.build\publish\app – außerhalb des Repos, weil X: eine SMB-Freigabe ist.
# Die CI übergibt mit -Output einen Ordner im Arbeitsverzeichnis.
#
# Native AOT braucht den MSVC-Linker (Visual Studio Build Tools mit „Desktop development with C++“). Das ILCompiler-
# Paket sucht ihn über vswhere.exe, das nicht im PATH liegt – deshalb wird der Installer-Ordner hier ergänzt.
param(
    [string]$Output = (Join-Path $env:LOCALAPPDATA 'Momentum4Control.build\publish\app')
)

$ErrorActionPreference = 'Stop'
$installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (-not (Test-Path (Join-Path $installer 'vswhere.exe'))) {
    throw 'vswhere.exe nicht gefunden – Visual Studio Build Tools mit C++ installieren.'
}

$env:PATH = "$installer;$env:PATH"
$root = Split-Path $PSScriptRoot -Parent

dotnet publish (Join-Path $root 'src\Momentum4.App\Momentum4.App.csproj') -c Release -o $Output -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen ($LASTEXITCODE)." }

# m4ctl (Kommandozeile) als eigene AOT-Exe in denselben Ordner
dotnet publish (Join-Path $root 'src\Momentum4.Cli\Momentum4.Cli.csproj') -c Release -o $Output -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish m4ctl fehlgeschlagen ($LASTEXITCODE)." }

$exe = Join-Path $Output 'Momentum4Control.exe'
'{0} – Exe {1:N1} MB, Ordner {2:N1} MB' -f $exe, ((Get-Item $exe).Length / 1MB), ((Get-ChildItem $Output -Recurse | Measure-Object Length -Sum).Sum / 1MB)

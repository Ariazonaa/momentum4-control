; Inno-Setup-Skript für MOMENTUM 4 Control (inoffiziell).
; Erzeugt ein setup.exe, das die self-contained AOT-App per-User installiert (kein Admin/UAC).
; Aufruf über tools/make-release.ps1; die Variablen kommen per /D von ISCC:
;   ISCC /DAppVersion=1.1.0 /DSourceDir="...\publish\app" /DRepoDir="X:\src\sennheiser" /DOutputDir="...\dist" installer\Momentum4Control.iss
; Fallback-Werte, damit sich das Skript auch direkt in der Inno-IDE öffnen lässt:
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\..\AppData\Local\Momentum4Control.build\publish\app"
#endif
#ifndef RepoDir
  #define RepoDir ".."
#endif
#ifndef OutputDir
  #define OutputDir "dist"
#endif

#define AppName "MOMENTUM 4 Control"
#define AppExe "Momentum4Control.exe"
#define AppPublisher "Ariazonaa"
#define AppUrl "https://github.com/Ariazonaa/momentum4-control"

[Setup]
; Stabile AppId (nie ändern – daran erkennt Windows Upgrades und Deinstallation)
AppId={{A1F2C3D4-5E6F-47A8-9B0C-1D2E3F405162}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
VersionInfoVersion={#AppVersion}
; Per-User-Installation ohne Adminrechte → {autopf} = %LOCALAPPDATA%\Programs
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Momentum 4 Control
DefaultGroupName=MOMENTUM 4 Control
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile={#RepoDir}\src\Momentum4.App\Assets\App.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Momentum4Control-Setup-{#AppVersion}
; Laufende Tray-App vor dem Überschreiben schließen (bei Upgrades)
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Desktop-Verknüpfung anlegen"; GroupDescription: "Verknüpfungen:"; Flags: unchecked
Name: "autostart"; Description: "Mit Windows starten (minimiert ins Infobereich-Symbol)"; GroupDescription: "Autostart:"; Flags: unchecked

[Files]
; Der komplette self-contained Ordner – ohne Portable-Marker/Daten (die gehören nur in die portable Variante)
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "Momentum4Control.portable,Data\*"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} deinstallieren"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Autostart-Eintrag (dieselbe Stelle/Wert, den auch die App-Einstellungen verwalten)
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Momentum4Control"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{#AppName} jetzt starten"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Falls die App noch läuft, vor dem Entfernen beenden
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExe} /F"; Flags: runhidden; RunOnceId: "KillApp"

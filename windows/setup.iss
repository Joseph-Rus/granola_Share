; Study-Stash-Setup.exe: installs Study Stash for this Windows account only (no admin rights), with a
; Start Menu entry. On first open, the app installs its background helper itself.
; Built by windows/build.ps1 (Inno Setup 6): ISCC /DAppVersion=0.4.0 /DSource=<the built app> setup.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Source
  #define Source "..\dist\Study Stash"
#endif

[Setup]
AppId={{6F2C9A51-3B7E-4D28-9C41-8E5A0B7D2F63}
AppName=Study Stash
AppVersion={#AppVersion}
AppVerName=Study Stash {#AppVersion}
AppPublisher=Study Stash
AppPublisherURL=https://github.com/Joseph-Rus/study-stash
AppSupportURL=https://github.com/Joseph-Rus/study-stash/issues
AppCopyright=MIT License. Not affiliated with or endorsed by Granola.
; the same folder `granola-share client open --install` and auto-update use
DefaultDirName={localappdata}\Programs\Study Stash
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Study-Stash-Setup
SetupIconFile=..\granola_share\assets\study-stash.ico
UninstallDisplayIcon={app}\Study Stash.exe
UninstallDisplayName=Study Stash
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Add a desktop icon"; Flags: unchecked

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Study Stash"; Filename: "{app}\Study Stash.exe"; Comment: "Open Study Stash"
Name: "{userdesktop}\Study Stash"; Filename: "{app}\Study Stash.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Study Stash.exe"; Description: "Open Study Stash"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

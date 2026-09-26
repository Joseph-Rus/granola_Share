; The two Windows installers, one app: for this Windows account only (no admin rights), with a Start
; Menu entry.
;   Study-Stash-Laptop-Setup.exe   (Role=laptop)  the laptop you record on. On first open, the app
;                                                 installs its background helper itself.
;   Study-Stash-Library-Setup.exe  (Role=library) the PC that keeps your library. On first open, the
;                                                 app runs the library's setup in a window.
; Built by windows/build.ps1 (Inno Setup 6): ISCC /DAppVersion=0.4.1 /DSource=<the built app> /DRole=library setup.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Source
  #define Source "..\dist\Study Stash"
#endif
#ifndef Role
  #define Role "laptop"
#endif
#if Role == "library"
  #define Name "Study Stash Library"
  #define Id "{{A3D71E4C-5B92-4F0E-8C6A-1E9B2D4F7C85}"
  #define Output "Study-Stash-Library-Setup"
#else
  #define Name "Study Stash"
  #define Id "{{6F2C9A51-3B7E-4D28-9C41-8E5A0B7D2F63}"
  #define Output "Study-Stash-Laptop-Setup"
#endif

[Setup]
AppId={#Id}
AppName={#Name}
AppVersion={#AppVersion}
AppVerName={#Name} {#AppVersion}
AppPublisher=Study Stash
AppPublisherURL=https://github.com/Joseph-Rus/study-stash
AppSupportURL=https://github.com/Joseph-Rus/study-stash/issues
AppCopyright=MIT License. Not affiliated with or endorsed by Granola.
; the same folders `granola-share client open --install` and auto-update use
DefaultDirName={localappdata}\Programs\{#Name}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
OutputBaseFilename={#Output}
SetupIconFile=..\assets\study-stash.ico
UninstallDisplayIcon={app}\Study Stash.exe
UninstallDisplayName={#Name}
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
Name: "{userprograms}\{#Name}"; Filename: "{app}\Study Stash.exe"; Comment: "Open {#Name}"
Name: "{userdesktop}\{#Name}"; Filename: "{app}\Study Stash.exe"; Tasks: desktopicon

#if Role == "library"
[INI]
; tells the app it's the library's own window
Filename: "{app}\study-stash.ini"; Section: "app"; Key: "role"; String: "library"
#endif

[Run]
Filename: "{app}\Study Stash.exe"; Description: "Open {#Name}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

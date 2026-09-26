; One app, one AppId, two Setup.exe (built by windows/build.ps1 with Inno Setup 6):
;   Study-Stash-Laptop-Setup.exe   (Role=laptop)   the computer you record lectures on.
;   Study-Stash-Library-Setup.exe  (Role=library)  the computer that keeps your library.
; The same AppId for both means installing the other role's Setup.exe over an existing install is an
; in-place upgrade: it lands in the same folder and just rewrites study-stash.ini's role, so a student who
; picks the wrong one (or repurposes a computer) never ends up with two copies.
; Per-user install (no admin rights), so [Files] copies the tree for whichever processor this Windows is
; (an arm64 Windows can also run the x64 tree under emulation, but its own tree is faster).
;   ISCC /Qp /DAppVersion=0.4.4 /DSource=<dist\windows, holding win-x64\ and win-arm64\> /DRole=laptop|library /Odist\windows setup.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Source
  #define Source "..\dist\windows"
#endif
#ifndef Role
  #define Role "laptop"
#endif
#if Role == "library"
  #define Output "Study-Stash-Library-Setup"
#else
  #define Output "Study-Stash-Laptop-Setup"
#endif

[Setup]
AppId={{6F2C9A51-3B7E-4D28-9C41-8E5A0B7D2F63}
AppName=Study Stash
AppVersion={#AppVersion}
AppVerName=Study Stash {#AppVersion}
AppPublisher=Study Stash
AppPublisherURL=https://github.com/Joseph-Rus/study-stash
AppSupportURL=https://github.com/Joseph-Rus/study-stash/issues
AppCopyright=MIT License.
DefaultDirName={localappdata}\Programs\Study Stash
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputBaseFilename={#Output}
SetupIconFile=..\assets\study-stash.ico
UninstallDisplayIcon={app}\StudyStash.exe
UninstallDisplayName=Study Stash
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Add a desktop icon"; Flags: unchecked

[Files]
Source: "{#Source}\win-arm64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64
Source: "{#Source}\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64

[INI]
; setup starts from this; after first run the role lives in the app's own settings (Apps.RolePreset)
Filename: "{app}\study-stash.ini"; Section: "app"; Key: "role"; String: "{#Role}"
Filename: "{app}\study-stash.ini"; Section: "app"; Key: "version"; String: "{#AppVersion}"

[Icons]
Name: "{userprograms}\Study Stash"; Filename: "{app}\StudyStash.exe"; Comment: "Open Study Stash"
Name: "{userdesktop}\Study Stash"; Filename: "{app}\StudyStash.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\StudyStash.exe"; Description: "Open Study Stash"; Flags: nowait postinstall skipifsilent
; a silent update (/relaunch=1) reopens the app once the new files are in place
Filename: "{app}\StudyStash.exe"; Parameters: "--background"; Flags: nowait; Check: Relaunch

[UninstallRun]
; Restart Manager (CloseApplications) already closed a foreground copy; this catches one running in the
; tray with no window for it to find.
Filename: "{cmd}"; Parameters: "/c taskkill /f /im StudyStash.exe"; Flags: runhidden; RunOnceId: "KillStudyStash"

[UninstallDelete]
; only the app itself: lectures live in the home folder and Documents\Study Stash, untouched.
Type: filesandordirs; Name: "{app}"

[Code]
function Relaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// The app's own start-at-login entry (Desktop.StartAtLogin): removed only if it still points at this
// install, so a second profile's entry (a different value name) or someone else's app is left alone.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RunValue: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Study Stash', RunValue) then
      if Pos(ExpandConstant('{app}'), RunValue) > 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Study Stash');
end;

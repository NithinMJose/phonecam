[Setup]
AppId={{7B5E9A41-2C3D-4F88-9A1E-4D5F6A7B8C9D}
AppName=PhoneCam
AppVersion=1.2.3
AppPublisher=NithinMJose
DefaultDirName={autopf}\PhoneCam
DefaultGroupName=PhoneCam
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=output
OutputBaseFilename=PhoneCamSetup
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
SetupIconFile=..\windows-app\PhoneCam\Assets\PhoneCam.ico
UninstallDisplayIcon={app}\PhoneCam.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Start PhoneCam minimized in tray when Windows starts"; GroupDescription: "Startup:"

[Files]
Source: "staging\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\virtualcam-dll\build\Release\PhoneCamVirtualCam.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PhoneCam"; Filename: "{app}\PhoneCam.exe"
Name: "{group}\Uninstall PhoneCam"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PhoneCam"; Filename: "{app}\PhoneCam.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PhoneCam"; ValueData: """{app}\PhoneCam.exe"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\icacls.exe"; Parameters: """{app}\PhoneCamVirtualCam.dll"" /grant ""*S-1-5-19:(RX)"" /grant ""*S-1-15-2-1:(RX)"""; Flags: runhidden waituntilterminated
Filename: "{sys}\regsvr32.exe"; Parameters: "/s ""{app}\PhoneCamVirtualCam.dll"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PhoneCam"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""PhoneCam"" dir=in action=allow program=""{app}\PhoneCam.exe"" enable=yes profile=any"; Flags: runhidden waituntilterminated
Filename: "{app}\PhoneCam.exe"; Description: "{cm:LaunchProgram,PhoneCam}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM PhoneCam.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillPhoneCam"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM adb.exe"; Flags: runhidden waituntilterminated; RunOnceId: "KillAdb"
Filename: "{sys}\regsvr32.exe"; Parameters: "/s /u ""{app}\PhoneCamVirtualCam.dll"""; Flags: runhidden waituntilterminated; RunOnceId: "UnregPhoneCam"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PhoneCam"""; Flags: runhidden waituntilterminated; RunOnceId: "DelFwPhoneCam"


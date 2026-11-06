; Inno Setup script for BingeRide-Setup.exe. Build it with scripts/build-installer.ps1.
; Installs for the current user, so it does not need admin rights.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\win-x64\BingeRide.exe"
#endif

[Setup]
; Never change AppId: Windows uses it to recognise an update to an installed copy.
AppId={{6F3B8E52-4C1D-4A7E-9B26-2D5E8C1A7F40}
AppName=BingeRide
AppVersion={#AppVersion}
AppPublisher=Ido Grinblat
AppPublisherURL=https://github.com/idog2k/BingeRide
AppSupportURL=https://github.com/idog2k/BingeRide/issues
AppUpdatesURL=https://github.com/idog2k/BingeRide/releases/latest
; Must match MutexName in src/BingeRide.Windows/SingleInstance.cs: Setup waits until the app is closed.
AppMutex=Local\BingeRide.SingleInstance
; With PrivilegesRequired=lowest, {autopf} is %LOCALAPPDATA%\Programs.
DefaultDirName={autopf}\BingeRide
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; The Windows head targets net10.0-windows10.0.19041.0, win-x64.
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=BingeRide-Setup
SetupIconFile=..\src\BingeRide.App\Assets\app.ico
UninstallDisplayIcon={app}\BingeRide.exe
UninstallDisplayName=BingeRide
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: desktopicons; Description: "Add BingeRide and BingeRide Overlay shortcuts to the desktop"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "BingeRide.exe"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\BingeRide"; Filename: "{app}\BingeRide.exe"
Name: "{autoprograms}\BingeRide Overlay"; Filename: "{app}\BingeRide.exe"; Parameters: "--overlay"
Name: "{autodesktop}\BingeRide"; Filename: "{app}\BingeRide.exe"; Tasks: desktopicons
Name: "{autodesktop}\BingeRide Overlay"; Filename: "{app}\BingeRide.exe"; Parameters: "--overlay"; Tasks: desktopicons

[Run]
Filename: "{app}\BingeRide.exe"; Description: "Start BingeRide"; Flags: nowait postinstall skipifsilent

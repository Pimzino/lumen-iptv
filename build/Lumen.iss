; Inno Setup script for Lumen IPTV.
;
; Why Inno Setup (not MSIX): Lumen ships as a self-contained .NET deployment bundled with
; VideoLAN's native LibVLC, whose plugins tree is a large, flat set of DLLs. Inno Setup packages
; that payload into one straightforward, offline, un-signed-friendly installer without MSIX's
; packaging graph, virtualized filesystem, or store/enterprise signing requirements — the right
; fit for a self-hosted desktop app of this shape.
;
; Build the payload first:
;   dotnet publish src/Lumen.App -c Release -p:PublishProfile=win-x64
; Then compile this script with the Inno Setup Compiler (iscc build/Lumen.iss).
;
; The version can be overridden from the command line without editing this file:
;   iscc /DAppVersion=1.2.3 build/Lumen.iss

#define AppName "Lumen"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppPublisher "Lumen"
#define AppExeName "Lumen.exe"

[Setup]
AppId={{9B3F5B1E-3C1D-49D2-9C0E-4C8DFF000001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/Pimzino/lumen-iptv
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\artifacts\installer
OutputBaseFilename=Lumen-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
MinVersion=10.0.17763
SetupIconFile=..\src\Lumen.App\Assets\lumen.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

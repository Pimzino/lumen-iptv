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
  #define AppVersion "0.1.2"
#endif
#define AppPublisher "Lumen"
#define AppExeName "Lumen.exe"

[Setup]
AppId={{9B3F5B1E-3C1D-49D2-9C0E-4C8DFF000001}
AppName={#AppName}
AppVersion={#AppVersion}
; Pin the display title to the bare name. Left unset, Inno Setup derives AppVerName
; as "{#AppName} version {#AppVersion}" and uses it as the DisplayName in Add/Remove
; Programs, the wizard, and the uninstaller — showing "Lumen version X.Y.Z" as the app
; name. AppVersion still fills the separate DisplayVersion (the "Version" column).
AppVerName={#AppName}
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
; Silent installs (the in-app auto-updater runs the setup with /SILENT) skip the entry above.
; Relaunch Lumen after such an update, as the original non-elevated user rather than as admin.
Filename: "{app}\{#AppExeName}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Code]
// True if the uninstaller was launched with the given command-line switch,
// e.g. HasUninstallSwitch('/REMOVEDATA').
function HasUninstallSwitch(const Switch: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Switch) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

// On uninstall, decide the fate of the user's personal data — library, settings,
// logs, and cached images in %LocalAppData%\Lumen (see AppPaths.DataRoot in
// src/Lumen.Core/AppPaths.cs), which is separate from the installed program files:
//
//   * "/REMOVEDATA" on the command line  -> delete it without prompting. This gives
//                                           silent/scripted uninstalls a way to opt in.
//   * silent uninstall, no flag          -> keep it (never destroy data unasked).
//   * interactive uninstall, no flag     -> ask, defaulting to keep.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  RemoveData: Boolean;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  DataDir := ExpandConstant('{localappdata}\Lumen');
  if not DirExists(DataDir) then
    Exit;

  if HasUninstallSwitch('/REMOVEDATA') then
    RemoveData := True
  else if UninstallSilent then
    RemoveData := False
  else
    // Default to "No" (keep) so an accidental Enter can't destroy the library.
    RemoveData := MsgBox('Do you want to remove your Lumen data?' + #13#10#13#10 +
                         'This includes your library, settings, logs, and cached images stored in:' + #13#10 +
                         DataDir + #13#10#13#10 +
                         'Click Yes to delete it, or No to keep it for a future reinstall.',
                         mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

  if RemoveData then
    DelTree(DataDir, True, True, True);
end;

; SaveTether's Windows installer (desktop-app-spec.md, section 11, "Installing"; decided by the owner on 2026-11-19).
; Per user, no administrator: everything in %LOCALAPPDATA%\Programs\SaveTether, a Start menu entry, an entry in
; Settings > Apps that uninstalls it, and an opt-in, unticked task that adds the folder to the user's Path
; (decision 10). Built from an unpacked release package by build/build-installer.ps1:
;
;   ISCC.exe /DAppVersion=<version> /DSourceDir=<unpacked package> /DOutputDir=<dir> /DOutputName=<name> build\windows\savetether.iss

#ifndef AppVersion
  #error AppVersion must be given: /DAppVersion=0.3.0
#endif
#ifndef SourceDir
  #error SourceDir must be given: the unpacked package folder
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef OutputName
  #define OutputName "savetether-setup"
#endif

[Setup]
; The AppId ties versions together, so a newer installer upgrades the installed one in place. Never change it.
AppId={{6E3C9B52-8F1D-4A7E-9C41-2B5D7E0A9F13}
AppName=SaveTether
AppVersion={#AppVersion}
AppVerName=SaveTether {#AppVersion}
AppPublisher=SaveTether contributors
AppPublisherURL=https://github.com/carlocgc/SaveTether
AppSupportURL=https://github.com/carlocgc/SaveTether/issues
DefaultDirName={localappdata}\Programs\SaveTether
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#SourceDir}\LICENSE
UninstallDisplayName=SaveTether
UninstallDisplayIcon={app}\savetether-gui.exe
; A running SaveTether is closed before its files are replaced or removed. It quits when Windows asks
; (it never vetoes the end of a session), and is not restarted: the last page offers to start it.
CloseApplications=yes
RestartApplications=no
ChangesEnvironment=yes

[Tasks]
Name: "addtopath"; Description: "Make the savetether command available in terminals (adds the install folder to your Path)"; Flags: unchecked

[Files]
Source: "{#SourceDir}\savetether-gui.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\savetether.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\libSkiaSharp.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\libHarfBuzzSharp.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\av_libglesv2.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SaveTether"; Filename: "{app}\savetether-gui.exe"; Comment: "Keeps game save folders linked to one synced folder"

[Run]
Filename: "{app}\savetether-gui.exe"; Description: "Start SaveTether"; Flags: nowait postinstall skipifsilent

[Code]
const
  EnvironmentKey = 'Environment';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  { Where the installer notes that it added the folder to Path itself, so uninstalling removes only what it added:
    someone who had put the folder on Path by hand keeps it. }
  MarkerKey = 'Software\SaveTether';
  MarkerValue = 'AddedToPath';

{ One Path entry, compared without letter case or a trailing backslash. }
function SameFolder(Entry, Folder: string): Boolean;
begin
  Entry := Trim(Entry);
  if (Length(Entry) > 3) and (Entry[Length(Entry)] = '\') then
    Entry := Copy(Entry, 1, Length(Entry) - 1);
  Result := CompareText(Entry, Folder) = 0;
end;

{ The user's Path as written, without expanding %VAR% entries. }
function ReadUserPath(): string;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Result) then
    Result := '';
end;

function PathHas(Paths, Folder: string): Boolean;
var
  Rest, Entry: string;
  Cut: Integer;
begin
  Result := False;
  Rest := Paths;
  while Rest <> '' do
  begin
    Cut := Pos(';', Rest);
    if Cut = 0 then
    begin
      Entry := Rest;
      Rest := '';
    end
    else
    begin
      Entry := Copy(Rest, 1, Cut - 1);
      Rest := Copy(Rest, Cut + 1, Length(Rest));
    end;
    if SameFolder(Entry, Folder) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

{ Written as an expandable string when an entry uses %VAR%, so it keeps working; otherwise as a plain string. }
procedure WriteUserPath(Paths: string);
begin
  if Pos('%', Paths) > 0 then
    RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Paths)
  else
    RegWriteStringValue(HKCU, EnvironmentKey, 'Path', Paths);
end;

{ Adds the folder, and notes that it did. A folder already on Path is left as it is, with no note. }
procedure AddToPath(Folder: string);
var
  Paths: string;
begin
  Paths := ReadUserPath();
  if PathHas(Paths, Folder) then
    Exit;
  if (Paths <> '') and (Paths[Length(Paths)] <> ';') then
    Paths := Paths + ';';
  WriteUserPath(Paths + Folder);
  RegWriteStringValue(HKCU, MarkerKey, MarkerValue, Folder);
end;

procedure RemoveFromPath(Folder: string);
var
  Paths, Rest, Entry, Kept: string;
  Cut: Integer;
begin
  Paths := ReadUserPath();
  if not PathHas(Paths, Folder) then
    Exit;
  Kept := '';
  Rest := Paths;
  while Rest <> '' do
  begin
    Cut := Pos(';', Rest);
    if Cut = 0 then
    begin
      Entry := Rest;
      Rest := '';
    end
    else
    begin
      Entry := Copy(Rest, 1, Cut - 1);
      Rest := Copy(Rest, Cut + 1, Length(Rest));
    end;
    if not SameFolder(Entry, Folder) then
    begin
      if Kept <> '' then
        Kept := Kept + ';';
      Kept := Kept + Entry;
    end;
  end;
  { Nothing left: the user had no Path of their own before the installer added one, so there is none again. }
  if Kept = '' then
    RegDeleteValue(HKCU, EnvironmentKey, 'Path')
  else
    WriteUserPath(Kept);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddToPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Started, Added: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Only an entry this installer added; one put there by hand stays. }
    if RegQueryStringValue(HKCU, MarkerKey, MarkerValue, Added) and SameFolder(Added, ExpandConstant('{app}')) then
    begin
      RemoveFromPath(ExpandConstant('{app}'));
      RegDeleteValue(HKCU, MarkerKey, MarkerValue);
      RegDeleteKeyIfEmpty(HKCU, MarkerKey);
    end;
    { The app's "Start with the session" entry, when it starts the copy being removed: it would point at nothing. }
    if RegQueryStringValue(HKCU, RunKey, 'SaveTether', Started)
      and (Pos(Uppercase(ExpandConstant('{app}') + '\'), Uppercase(Started)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'SaveTether');
  end;
end;

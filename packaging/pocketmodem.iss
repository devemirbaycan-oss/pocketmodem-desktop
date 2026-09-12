; PocketModem Windows installer (Inno Setup 6)
;
; Build with:  iscc packaging\pocketmodem.iss
; Output:      packaging\output\PocketModem-Setup-<version>.exe
;
; The installer is unsigned. Windows SmartScreen will warn on first run until
; the binary builds reputation or a certificate is bought; the README explains
; the "More info -> Run anyway" path rather than leaving people stuck at a
; dialog that looks like a virus warning.

#define AppName "PocketModem"
#define AppVersion "1.0.0"
#define AppPublisher "PocketModem"
#define AppExeName "PocketModem-Desktop.exe"
#define CliExeName "pocketmodem.exe"

[Setup]
AppId={{8C4E2F91-3A7D-4B62-9E15-D8A6C7B40F23}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=PocketModem-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; 64-bit only: Wintun ships as amd64, and the tunnel has never been built for
; anything else.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Installing to Program Files needs elevation, and so does the adapter the app
; creates at runtime.
PrivilegesRequired=admin

; The wizard icon and the entry in Add/Remove Programs.
SetupIconFile=..\windows\PocketModem.App\Assets\pocketmodem.ico
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile=..\LICENSE.txt
InfoBeforeFile=before-install.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "addtopath"; Description: "Add the pocketmodem command to PATH"; GroupDescription: "Command line:"; Flags: unchecked

[Files]
Source: "..\dist\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\{#CliExeName}"; DestDir: "{app}"; Flags: ignoreversion

; Wintun is the virtual network driver the tunnel is built on. Bundling it
; means users are not sent to another site to make the app work at all.
Source: "..\dist\wintun.dll"; DestDir: "{app}"; Flags: ignoreversion

; Avalonia's native rendering libraries.
Source: "..\dist\av_libglesv2.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\libHarfBuzzSharp.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\libSkiaSharp.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

; Recovery script, so a user whose routing is stranded has something to run
; without needing the command line.
Source: "FIX-MY-INTERNET.bat"; DestDir: "{app}"; Flags: ignoreversion

Source: "..\README.md"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Fix my internet"; Filename: "{app}\FIX-MY-INTERNET.bat"; Comment: "Restore normal routing if PocketModem was interrupted"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Optional PATH entry for the console tool.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
    Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Undo any routing the tunnel left behind before removing the files, or an
; interrupted session could leave the machine unable to reach anything.
Filename: "{app}\{#CliExeName}"; Parameters: "recover"; Flags: runhidden; RunOnceId: "RestoreRouting"

[Code]
function NeedsAddPath(Param: string): boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
    'Path', OrigPath)
  then begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Param + ';', ';' + OrigPath + ';') = 0;
end;

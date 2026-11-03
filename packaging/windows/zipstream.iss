; Instalador Windows do ZipStream (Inno Setup 6).
; Gerado pelo workflow de release; localmente:
;   iscc /DAppVersion=1.2.3 /DSourceDir=C:\caminho\publish\win-x64 packaging\windows\zipstream.iss
; Instala por usuário (sem administrador), igual ao menu de contexto, que fica em HKCU.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\win-x64"
#endif

#define AppName "ZipStream"
#define AppExe "ZipP2P.App.exe"
; Mesmo verbo de WindowsShellIntegration.VerbName.
#define VerbName "ZipStream.Send"

[Setup]
AppId={{6D0B7C1E-4F3A-4B8E-9C52-2A7E1D9F4B63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Igor Silva
AppPublisherURL=https://github.com/igoreusttaquio/ZipStream-P2P
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\..\publish
OutputBaseFilename=ZipStream-Setup-{#AppVersion}-win-x64
SetupIconFile=..\..\src\ZipP2P.App\Assets\zipstream.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Plano B para fechar o app: o principal é CloseRunningApp ([Code]), porque o app fica na bandeja
; e o pedido de fechar janela do Windows só o esconde.
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.SendWith=Send with ZipStream
ptbr.SendWith=Enviar com ZipStream
es.SendWith=Enviar con ZipStream
en.ContextMenuTask=Add "Send with ZipStream" to the right-click menu
ptbr.ContextMenuTask=Adicionar "Enviar com ZipStream" ao menu do botão direito
es.ContextMenuTask=Agregar "Enviar con ZipStream" al menú del botón derecho

[Tasks]
Name: "contextmenu"; Description: "{cm:ContextMenuTask}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Atualização: remove DLLs da versão anterior que não existem mais.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Mesmo formato de WindowsShellIntegration (o app mostra "instalado" em Configurações).
Root: HKCU; Subkey: "Software\Classes\*\shell\{#VerbName}"; ValueType: string; ValueName: ""; ValueData: "{cm:SendWith}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\{#VerbName}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\*\shell\{#VerbName}\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""--send"" ""%1"""; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\{#VerbName}"; ValueType: string; ValueName: ""; ValueData: "{cm:SendWith}"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\{#VerbName}"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: contextmenu
Root: HKCU; Subkey: "Software\Classes\Directory\shell\{#VerbName}\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""--send"" ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Atualização iniciada pelo próprio app (UpdateInstaller: /SILENT /relaunch=1): abre o app de novo no fim.
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: ShouldRelaunch

[Code]
const
  // Mesmo nome de Program.AppMutexName: existe enquanto o app está aberto.
  AppMutex = 'ZipStreamAppMutex';

// Pede ao app aberto que saia de verdade (--close-for-update) e espera até 15 s pelo fim dele.
// Versões antigas não criam o mutex; para elas fica o CloseApplications.
procedure CloseRunningApp();
var
  Exe: String;
  ResultCode, Waited: Integer;
begin
  if not CheckForMutexes(AppMutex) then
    Exit;

  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--close-for-update', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Waited := 0;
  while CheckForMutexes(AppMutex) and (Waited < 15000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
end;

function ShouldRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseRunningApp();
  Result := True;
end;

// Remove o menu de contexto também quando ele foi ativado depois, pelas Configurações do app.
// As configurações e transferências em %LOCALAPPDATA%\ZipStream são mantidas.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\*\shell\{#VerbName}');
    RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Directory\shell\{#VerbName}');
  end;
end;

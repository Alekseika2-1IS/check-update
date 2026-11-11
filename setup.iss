#ifndef AppVersion
  #define AppVersion "0.6.0"
#endif
#ifndef BuildDir
  #define BuildDir "dist"
#endif
#ifdef TestInstall
  #define ProductName "Check Update Installer Test"
  #define ProductId "CheckUpdateInstallerTest"
#else
  #define ProductName "Check Update"
  #define ProductId "CheckUpdate"
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher=Check Update
AppPublisherURL=https://github.com/Alekseika2-1IS/check-update
AppSupportURL=https://github.com/Alekseika2-1IS/check-update/issues
AppUpdatesURL=https://github.com/Alekseika2-1IS/check-update/releases/latest
DefaultDirName={localappdata}\Programs\{#ProductName}
DefaultGroupName={#ProductName}
PrivilegesRequired=lowest
MinVersion=10.0
WizardStyle=modern dark polar includetitlebar
WizardSizePercent=110
SetupIconFile=assets\app.ico
WizardSmallImageFile=assets\app.png
DisableWelcomePage=yes
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
UsePreviousTasks=yes
UninstallDisplayIcon={app}\Check Update.exe
UninstallDisplayName={#ProductName}
OutputDir={#BuildDir}
OutputBaseFilename=Check-Update-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
UninstallLogMode=append
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#BuildDir}\Check Update.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#ProductName}"; Filename: "{app}\Check Update.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\{#ProductName}"; Filename: "{app}\Check Update.exe"; WorkingDir: "{app}"; Check: DesktopShortcutWanted

[Run]
Filename: "{app}\Check Update.exe"; Description: "Запустить Check Update"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\Check Update.exe.previous"

[Messages]
ReadyLabel1=Всё готово для установки Check Update
ReadyLabel2a=Программа будет установлена только для текущего пользователя. Права администратора не требуются.%n%nНажмите «Установить», чтобы продолжить.
FinishedHeadingLabel=Check Update установлен
FinishedLabelNoIcons=Готово! Check Update доступен в меню «Пуск».%n%nОбновления самого приложения можно скачивать из его окна.

[Code]
var
  DesktopShortcut: TNewCheckBox;
  InstallDescription: TNewStaticText;
  DirectoryPageShown: Boolean;

function GetProbeFileName(Path, Prefix: String; Unique: Cardinal; Buffer: String): Cardinal;
  external 'GetTempFileNameW@kernel32.dll stdcall';

function DirectoryAccessError(Directory: String): String;
var
  Existing, Parent, Probe: String;
begin
  Result := '';
  Existing := RemoveBackslashUnlessRoot(Directory);
  while not DirExists(Existing) do
  begin
    Parent := ExtractFileDir(Existing);
    if (Parent = '') or (Parent = Existing) then
    begin
      Result := 'Выбранный диск или папка недоступны. Выберите другую папку.';
      Exit;
    end;
    Existing := Parent;
  end;
  Probe := StringOfChar(#0, 260);
  if GetProbeFileName(Existing, 'cup', 0, Probe) = 0 then
  begin
    Result := 'Нет доступа для записи в выбранную папку. Выберите папку, доступную вашему пользователю, например на диске D: или в вашей пользовательской папке.';
    Exit;
  end;
  Probe := Copy(Probe, 1, Pos(#0, Probe) - 1);
  if not DeleteFile(Probe) then
    Result := 'Не удалось проверить доступ к выбранной папке. Выберите другую папку.';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if Trim(WizardDirValue) = '' then
      Error := 'Укажите папку установки вручную или выберите её кнопкой «Обзор».'
    else
      Error := DirectoryAccessError(WizardDirValue);
    if Error <> '' then
    begin
      MsgBox(Error, mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := DirectoryAccessError(ExpandConstant('{app}'));
end;

function InitializeSetup(): Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    MsgBox('Для Check Update требуется .NET Framework 4.8. Установите его с сайта Microsoft и повторите установку.', mbError, MB_OK);
end;

procedure InitializeWizard();
begin
  WizardForm.ReadyMemo.Visible := False;
  WizardForm.ReadyMemo.TabStop := False;
  WizardForm.ReadyLabel.Visible := False;
  InstallDescription := TNewStaticText.Create(WizardForm);
  InstallDescription.Parent := WizardForm.ReadyPage;
  InstallDescription.Left := WizardForm.ReadyMemo.Left;
  InstallDescription.Top := ScaleY(12);
  InstallDescription.Width := WizardForm.ReadyMemo.Width;
  InstallDescription.Height := WizardForm.ReadyPage.Height - ScaleY(62);
  InstallDescription.AutoSize := False;
  InstallDescription.WordWrap := True;
  DesktopShortcut := TNewCheckBox.Create(WizardForm);
  DesktopShortcut.Parent := WizardForm.ReadyPage;
  DesktopShortcut.Left := WizardForm.ReadyMemo.Left;
  DesktopShortcut.Top := WizardForm.ReadyPage.Height - ScaleY(28);
  DesktopShortcut.Width := WizardForm.ReadyMemo.Width;
  DesktopShortcut.Caption := 'Создать ярлык на рабочем столе';
  DesktopShortcut.Checked := ExpandConstant('{param:DESKTOP|0}') = '1';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectDir) and (not DirectoryPageShown) then
  begin
    DirectoryPageShown := True;
    if (not WizardSilent) and (ExpandConstant('{param:DIR|}') = '') and
       (not RegKeyExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#ProductId}_is1')) then
      WizardForm.DirEdit.Text := '';
  end;
  if CurPageID = wpReady then
  begin
    WizardForm.ReadyMemo.Visible := False;
    WizardForm.ReadyLabel.Visible := False;
    InstallDescription.Caption := 'Проверяйте обновления программ и устанавливайте новые версии в одном месте.' + #13#10 + #13#10 +
      'Установка только для вас — права администратора не требуются.' + #13#10 + #13#10 +
      'Приложение появится в меню «Пуск». Удалить его можно через настройки Windows.' + #13#10 + #13#10 +
      'Папка установки:' + #13#10 + ExpandConstant('{app}') + #13#10 + #13#10 +
      'Нажмите «Установить», чтобы продолжить.';
  end;
end;

function DesktopShortcutWanted(): Boolean;
begin
  Result := DesktopShortcut.Checked;
end;

; CaptureIt — Windows installer (Inno Setup 6)
; Built in CI:  ISCC.exe /DAppVersion=x.y.z installer\CaptureIt.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8F4B2C6E-1A3D-4E7F-9B5C-4CA97B2E1026}
AppName=CaptureIt
AppVersion={#AppVersion}
AppVerName=CaptureIt {#AppVersion}
AppPublisher=Heechan Jeong
AppPublisherURL=https://github.com/th00tames1/CaptureIt
AppSupportURL=https://github.com/th00tames1/CaptureIt/issues
DefaultDirName={autopf}\CaptureIt
DisableProgramGroupPage=yes
; 관리자 권한 불필요 — 사용자 폴더에 설치되어 누구나 바로 설치 가능
PrivilegesRequired=lowest
OutputBaseFilename=CaptureIt-Setup-{#AppVersion}-win-x64
OutputDir=..\dist
SetupIconFile=..\CaptureIt\app.ico
UninstallDisplayIcon={app}\CaptureIt.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
; 인앱 업데이트: 실행 중인 이전 버전을 닫고 교체한다
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\win\CaptureIt.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\CaptureIt"; Filename: "{app}\CaptureIt.exe"
Name: "{autodesktop}\CaptureIt"; Filename: "{app}\CaptureIt.exe"; Tasks: desktopicon

[Run]
; skipifsilent 없음: 인앱 업데이트(/SILENT) 후에도 자동으로 다시 실행된다
Filename: "{app}\CaptureIt.exe"; Description: "{cm:LaunchProgram,CaptureIt}"; Flags: nowait postinstall

[UninstallRun]
; 실행 중이면 종료
Filename: "{cmd}"; Parameters: "/C taskkill /IM CaptureIt.exe /F"; Flags: runhidden; RunOnceId: "KillApp"

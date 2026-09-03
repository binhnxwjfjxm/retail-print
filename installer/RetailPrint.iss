#define MyAppName "Retail Print"
#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif

[Setup]
AppId={{B384AA5D-88B8-4C3D-9064-41F0F5F49061}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Nguyên Liệu Hưng Phát
DefaultDirName={localappdata}\Programs\RetailPrint
DefaultGroupName=Retail Print
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish\installer
OutputBaseFilename=RetailPrint-Setup-win-x64
SetupIconFile=..\RetailPrint\Assets\RetailPrint.ico
UninstallDisplayIcon={app}\RetailPrint.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài màn hình"; GroupDescription: "Lối tắt:"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\RetailPrint.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Retail Print"; Filename: "{app}\RetailPrint.exe"
Name: "{userdesktop}\Retail Print"; Filename: "{app}\RetailPrint.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RetailPrint.exe"; Description: "Mở Retail Print"; Flags: nowait postinstall skipifsilent

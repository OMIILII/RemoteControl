; installer.nsi - 用 NSIS 把 client/ (仅客户端) 打包成单文件安装包
; 运行: makensis.exe installer.nsi
; 产物: D:\ai\remote-desktop\packaging\RemoteControl-Setup.exe

!include "MUI2.nsh"
Unicode true

;------------------------------- 基本信息
Name "RemoteControl 远程控制"
OutFile "D:\ai\remote-desktop\packaging\RemoteControl-Setup.exe"
InstallDir "$LOCALAPPDATA\RemoteControl"   ; 用户级，免管理员/UAC
RequestExecutionLevel user
CRCCheck on

;------------------------------- 压缩（LZMA 固体压缩，体积小）
SetCompressor lzma

;------------------------------- MUI 界面
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

;------------------------------- 安装段
Section "主程序 (必选)" SecMain
  SetOutPath "$INSTDIR"
  ; 递归打包 publish_v2/ 下全部文件
  File /r "..\publish_v2\*.*"
  ; 移除不需要的文件
  Delete "$INSTDIR\signaling_server.exe"
  Delete "$INSTDIR\signaling_server.py"

  ; 开始菜单 + 桌面快捷方式
  CreateDirectory "$SMPROGRAMS\RemoteControl"
  CreateShortcut "$SMPROGRAMS\RemoteControl\RemoteControl.lnk" "$INSTDIR\RemoteControl.exe"
  CreateShortcut "$DESKTOP\RemoteControl.lnk" "$INSTDIR\RemoteControl.exe"

  ; 卸载程序与注册表项（HKCU，免管理员）
  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl" "DisplayName" "RemoteControl 远程控制"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl" "DisplayIcon" "$INSTDIR\RemoteControl.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl" "Publisher" "RemoteControl"
SectionEnd

;------------------------------- 卸载段
Section "Uninstall"
  Delete "$SMPROGRAMS\RemoteControl\RemoteControl.lnk"
  Delete "$DESKTOP\RemoteControl.lnk"
  RMDir "$SMPROGRAMS\RemoteControl"
  RMDir /r "$INSTDIR"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl"
SectionEnd

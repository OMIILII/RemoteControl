@echo off
REM ---------------------------------------------------------------
REM RemoteControl 卸载脚本（位于安装目录内，由控制面板-程序 调用）
REM 关闭进程 -> 删除目录 -> 删除快捷方式 -> 删除卸载注册表项。
REM ---------------------------------------------------------------
setlocal EnableExtensions
set "DEST=%~dp0"
if "%DEST:~-1%"=="\" set "DEST=%DEST:~0,-1%"

echo 正在卸载 RemoteControl ...
taskkill /IM RemoteControl.exe /F 2>nul

REM 把目录搬到临时区再删，避免正在运行的本脚本被锁
set "TRASH=%TEMP%\RemoteControl_uninst_%RANDOM%"
if exist "%DEST%" move "%DEST%" "%TRASH%" >nul 2>&1
if exist "%TRASH%" rmdir /S /Q "%TRASH%" >nul 2>&1
if exist "%DEST%" rmdir /S /Q "%DEST%" >nul 2>&1

del /Q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\RemoteControl.lnk" 2>nul
del /Q "%USERPROFILE%\Desktop\RemoteControl.lnk" 2>nul

reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl" /F >nul 2>&1

echo 卸载完成。
pause
endlocal

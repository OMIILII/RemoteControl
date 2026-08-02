@echo off
REM ---------------------------------------------------------------
REM RemoteControl 安装引导脚本（由 iexpress 自解压后调用）
REM 把解压出的全部文件复制到安装目录，并调用 PowerShell 建快捷方式。
REM ---------------------------------------------------------------
setlocal EnableExtensions
set "SRC=%~dp0"
if "%SRC:~-1%"=="\" set "SRC=%SRC:~0,-1%"

REM 目标目录：优先 Program Files，无权限则退到 LocalAppData
set "DEST=%ProgramFiles%\RemoteControl"
mkdir "%DEST%" 2>nul
if not exist "%DEST%\" (
    set "DEST=%LocalAppData%\RemoteControl"
    mkdir "%DEST%" 2>nul
)

echo 正在安装 RemoteControl 到 "%DEST%" ...
robocopy "%SRC%" "%DEST%" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /nc /ns /np >nul

if not exist "%DEST%\RemoteControl.exe" (
    echo 安装失败：未能复制 RemoteControl.exe
    pause
    exit /b 1
)

echo 正在创建快捷方式与卸载项 ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%DEST%\make_shortcuts.ps1" "%DEST%"

echo.
echo 安装完成！可在开始菜单 / 桌面找到 "RemoteControl"。
echo 运行后选择「被控端(Host)」或「控制端(Viewer)」。
echo.
pause
endlocal

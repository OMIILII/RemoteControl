# ---------------------------------------------------------------
# RemoteControl 安装后处理：创建开始菜单 / 桌面快捷方式，
# 并在 控制面板-程序 中注册卸载项（HKCU，免管理员）。
# 参数 $Dest = 安装目录（含 RemoteControl.exe）
# ---------------------------------------------------------------
param([string]$Dest)

$ErrorActionPreference = 'SilentlyContinue'

if (-not $Dest) { $Dest = $env:ProgramFiles + '\RemoteControl' }
$exe = Join-Path $Dest 'RemoteControl.exe'
if (-not (Test-Path $exe)) { Write-Host "找不到 $exe"; exit 1 }

$startMenu = [System.Environment]::GetFolderPath('StartMenu')
$desktop   = [System.Environment]::GetFolderPath('Desktop')
$programs  = Join-Path $startMenu 'Programs'

function New-Shortcut {
    param([string]$Name, [string]$Folder)
    if (-not (Test-Path $Folder)) { New-Item -ItemType Directory -Path $Folder -Force | Out-Null }
    $ws  = New-Object -ComObject WScript.Shell
    $lnk = Join-Path $Folder ($Name + '.lnk')
    $sc  = $ws.CreateShortcut($lnk)
    $sc.TargetPath       = $exe
    $sc.WorkingDirectory = $Dest
    $sc.Description       = '远程控制 RemoteControl'
    $sc.Save()
    Write-Host "已创建快捷方式: $lnk"
}

New-Shortcut 'RemoteControl' $programs
New-Shortcut 'RemoteControl' $desktop

# 注册卸载项（当前用户）
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RemoteControl'
New-Item -Path $key -Force | Out-Null
$uninstallCmd = 'cmd.exe /c "' + (Join-Path $Dest 'uninstall.cmd') + '"'
Set-ItemProperty -Path $key -Name 'DisplayName'     -Value 'RemoteControl 远程控制'
Set-ItemProperty -Path $key -Name 'UninstallString' -Value $uninstallCmd
Set-ItemProperty -Path $key -Name 'DisplayIcon'     -Value $exe
Set-ItemProperty -Path $key -Name 'InstallLocation' -Value $Dest
Set-ItemProperty -Path $key -Name 'Publisher'       -Value 'RemoteControl'
Set-ItemProperty -Path $key -Name 'NoModify'        -Value 1
Set-ItemProperty -Path $key -Name 'NoRepair'        -Value 1
Write-Host "已注册卸载项: $key"

#!/usr/bin/env python3
"""
make_installer.py - 用 NSIS 把【仅客户端】打包成单文件安装包。

流程：
  1. 把 publish/ 同步到 client/，自动排除服务端
     （signaling_server.exe / 启动信令服务器.bat），publish/ 原样保留。
  2. 用 NSIS (makensis) 依据 installer.nsi 把 client/ 打成单文件安装包。

产出：D:/ai/remote-desktop/packaging/RemoteControl-Setup.exe
特性：
  - 只包含客户端（Host + Viewer），不含信令/中继服务器
  - 安装到 LocalAppData\RemoteControl（用户级，免管理员/UAC）
  - 创建开始菜单 / 桌面快捷方式
  - 在「控制面板-程序」注册卸载项（uninstall.exe）
"""
import os, shutil, subprocess, sys

ROOT     = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# 同步源：优先用最新手动构建 publish_staged，回退到 build.py 产出的 publish。
PUBLISH  = os.path.join(ROOT, "publish_staged") if os.path.isdir(
    os.path.join(ROOT, "publish_staged")) else os.path.join(ROOT, "publish")
CLIENT   = os.path.join(ROOT, "client")
PKG      = os.path.join(ROOT, "packaging")
NSIS     = os.path.join(ROOT, "deps", "nsis-3.11", "nsis-3.11", "makensis.exe")

# 服务端文件：不打包进安装包（用户明确要求）。
# 覆盖信令服务器所有可能形态（.exe/.py）与启动脚本（.bat）。
SERVER_FILES = {
    "signaling_server.exe", "signaling_server.py", "signaling_server",
    "启动信令服务器.bat", "start_server.bat", "server.db",
}


def sync_client():
    """把 publish/ 同步到 client/，排除服务端文件；publish/ 原样保留。"""
    if not os.path.isdir(PUBLISH):
        print("[ERR] 找不到 publish/，请先运行 build.py", file=sys.stderr); sys.exit(1)
    if os.path.isdir(CLIENT):
        shutil.rmtree(CLIENT)
    os.makedirs(CLIENT, exist_ok=True)
    copied = 0
    skipped = []
    for root, dirs, fnames in os.walk(PUBLISH):
        for fn in fnames:
            rel = os.path.relpath(os.path.join(root, fn), PUBLISH)
            if os.path.dirname(rel) == "" and fn in SERVER_FILES:
                skipped.append(rel)
                continue
            src = os.path.join(root, fn)
            dst = os.path.join(CLIENT, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            shutil.copy2(src, dst)
            copied += 1
    print(f"[pkg] 已同步 {copied} 个客户端文件到 {CLIENT}")
    print(f"[pkg] 已排除服务端文件: {skipped}")


def main():
    if not os.path.exists(NSIS):
        print(f"[ERR] 找不到 makensis.exe: {NSIS}", file=sys.stderr); sys.exit(1)

    sync_client()

    nsi = os.path.join(PKG, "installer.nsi")
    if not os.path.exists(nsi):
        print(f"[ERR] 找不到 installer.nsi: {nsi}", file=sys.stderr); sys.exit(1)

    print(f"[pkg] 正在调用 NSIS 构建安装包（client 约 520MB，可能需要几分钟）...")
    rc = subprocess.run([NSIS, nsi])
    if rc.returncode != 0:
        print(f"[ERR] makensis 返回 {rc.returncode}", file=sys.stderr); sys.exit(rc.returncode)

    out_exe = os.path.join(PKG, "RemoteControl-Setup.exe")
    if os.path.exists(out_exe):
        sz = os.path.getsize(out_exe)
        print(f"[pkg] 安装包生成成功: {out_exe}  ({sz/1_000_000:.1f} MB)")
    else:
        print("[ERR] 未生成安装包 exe", file=sys.stderr); sys.exit(1)


if __name__ == "__main__":
    main()

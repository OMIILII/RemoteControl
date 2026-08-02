#!/usr/bin/env python3
"""
bootstrap.py - 下载编译环境和依赖（无需手动安装任何东西）。

在 Windows 上运行一次即可自动准备（gcc/MinGW 路线，走国内镜像）：
  * CMake（已就绪则跳过）
  * MSYS2（从中科大镜像下载，再用 pacman 装 gcc/cmake/ffmpeg 开发包）
  * .NET 8 SDK（用于编译 C# 界面，走微软官方 CDN）

所有内容下载到本项目的 deps/ 目录，不污染系统环境。
GitHub 资源走 ghproxy.net 代理；MSYS2 走中科大镜像；.NET 走微软 CDN。
完成后运行  python build.py  即可一键编译。

仅在 Windows 上有效（本软件就是 Windows 远程控制）。
"""

import os
import sys
import re
import json
import shutil
import zipfile
import urllib.request
import subprocess

ROOT = os.path.dirname(os.path.abspath(__file__))
DEPS = os.path.join(ROOT, "deps")
os.makedirs(DEPS, exist_ok=True)

ON_WIN = os.name == "nt"

# GitHub release 资产（/releases/download/）的国内代理前缀，按顺序尝试，最后回退直连。
GH_PROXIES = [
    "https://ghproxy.net/",
    "https://mirror.ghproxy.com/",
    "https://ghproxy.com/",
    "",
]


def log(msg):
    print(f"[bootstrap] {msg}", flush=True)


def error(msg):
    print(f"[bootstrap][ERROR] {msg}", file=sys.stderr, flush=True)


def run(cmd, **kw):
    log("运行: " + " ".join(cmd) if isinstance(cmd, list) else cmd)
    return subprocess.run(cmd, **kw)


def which(name):
    from shutil import which as _w
    return _w(name)


def _proxied(url):
    """对 GitHub release 资产走国内代理；其余直连。"""
    if "github.com" in url and "/releases/download/" in url:
        return [p + url for p in GH_PROXIES]
    return [url]


def _http_json(url):
    urls = _proxied(url) if ("github.com" in url) else [url]
    last = None
    for u in urls:
        try:
            req = urllib.request.Request(u, headers={"User-Agent": "rc-bootstrap/1.0"})
            with urllib.request.urlopen(req, timeout=30) as r:
                return json.loads(r.read().decode())
        except Exception as e:  # noqa
            last = e
            error(f"API 请求失败 {u}: {e}")
            continue
    raise last or RuntimeError("http json failed")


def download_file(url, dest, label=None, use_mirror=True):
    label = label or os.path.basename(url)
    if os.path.exists(dest):
        log(f"发现已存在文件(可能不完整)，删除后重新下载: {label}")
        try:
            os.remove(dest)
        except OSError:
            pass
    urls = _proxied(url) if use_mirror else [url]
    tmp = dest + ".part"
    last_err = None
    for u in urls:
        try:
            log(f"下载 {label} <- {u}")
            req = urllib.request.Request(u, headers={"User-Agent": "rc-bootstrap/1.0"})
            with urllib.request.urlopen(u, timeout=60) as r, open(tmp, "wb") as f:
                total = int(r.headers.get("Content-Length", 0) or 0)
                done = 0
                while True:
                    chunk = r.read(1024 * 256)
                    if not chunk:
                        break
                    f.write(chunk)
                    done += len(chunk)
                    if total:
                        sys.stderr.write(f"\r{label}: {done*100//total}%")
                        sys.stderr.flush()
                sys.stderr.write("\n")
            os.replace(tmp, dest)
            return dest
        except Exception as e:  # noqa
            last_err = e
            error(f"下载失败 {label}: {e}")
            if os.path.exists(tmp):
                try:
                    os.remove(tmp)
                except OSError:
                    pass
            continue
    raise last_err or RuntimeError(f"download failed: {label}")


def extract_zip(path, dest, flatten_inner=False):
    os.makedirs(dest, exist_ok=True)
    log(f"解压 {os.path.basename(path)} -> {dest}")
    with zipfile.ZipFile(path) as z:
        z.extractall(dest)
    if flatten_inner:
        entries = [e for e in os.listdir(dest) if os.path.isdir(os.path.join(dest, e))]
        if len(entries) == 1:
            inner = os.path.join(dest, entries[0])
            for name in os.listdir(inner):
                shutil.move(os.path.join(inner, name), os.path.join(dest, name))
            os.rmdir(inner)


# ----------------------------------------------------------------------- #
# 1. CMake（GitHub，ghproxy；已下好通常直接跳过）
# ----------------------------------------------------------------------- #
def ensure_cmake():
    if which("cmake"):
        log("检测到 cmake，跳过")
        return
    cmake_exe = os.path.join(DEPS, "cmake", "bin", "cmake.exe")
    if os.path.exists(cmake_exe):
        log("检测到本地 cmake，跳过")
        return
    ver = "3.30.0"
    url = f"https://github.com/Kitware/CMake/releases/download/v{ver}/cmake-{ver}-windows-x86_64.zip"
    zip_path = os.path.join(DEPS, "cmake.zip")
    download_file(url, zip_path, "cmake.zip")
    extract_zip(zip_path, os.path.join(DEPS, "cmake"), flatten_inner=True)
    os.remove(zip_path)
    log(f"cmake 就绪: {cmake_exe}")


# ----------------------------------------------------------------------- #
# 2. MSYS2（中科大镜像）+ pacman 安装 gcc/cmake/ffmpeg
# ----------------------------------------------------------------------- #
def _configure_pacman_mirror(msys):
    """写入中科大镜像并关闭签名校验（避免 keyring 初始化卡住；本机开发用途可接受）。"""
    d = os.path.join(msys, "etc", "pacman.d")
    with open(os.path.join(d, "mirrorlist.mingw64"), "w", encoding="utf-8") as f:
        f.write("Server = https://mirrors.ustc.edu.cn/msys2/mingw/x86_64/\n")
    with open(os.path.join(d, "mirrorlist.mingw32"), "w", encoding="utf-8") as f:
        f.write("Server = https://mirrors.ustc.edu.cn/msys2/mingw/i686/\n")
    with open(os.path.join(d, "mirrorlist"), "w", encoding="utf-8") as f:
        f.write("Server = https://mirrors.ustc.edu.cn/msys2/msys/$arch\n")
    conf = os.path.join(msys, "etc", "pacman.conf")
    with open(conf, "r", encoding="utf-8", errors="ignore") as f:
        txt = f.read()
    txt = txt.replace("SigLevel    = Required DatabaseOptional", "SigLevel    = Never")
    txt = txt.replace("SigLevel = Required DatabaseOptional", "SigLevel = Never")
    with open(conf, "w", encoding="utf-8") as f:
        f.write(txt)
    log("已配置中科大镜像并关闭 pacman 签名校验")


def ensure_msys2():
    msys = os.path.join(DEPS, "msys64")
    gcc = os.path.join(msys, "mingw64", "bin", "gcc.exe")
    if os.path.exists(gcc):
        log("检测到 MSYS2/gcc，跳过")
        return msys
    # 1) 安装器（中科大镜像，快）；已下载则跳过（避免重复下载大文件）
    url = "https://mirrors.ustc.edu.cn/msys2/distrib/msys2-x86_64-latest.exe"
    exe = os.path.join(DEPS, "msys2-installer.exe")
    if not (os.path.exists(exe) and os.path.getsize(exe) > 50_000_000):
        download_file(url, exe, "msys2-installer.exe", use_mirror=False)
    # 2) 静默安装到 deps/msys64（Inno Setup：/DIR 指定目录，/ALLUSERS=no 免提权）
    if os.path.exists(msys):
        shutil.rmtree(msys, ignore_errors=True)
    log("静默安装 MSYS2 …")
    rc = run([exe, "/VERYSILENT", "/SUPPRESSMSGBOXES", f"/DIR={msys}", "/ALLUSERS=no"])
    if rc.returncode != 0:
        error("MSYS2 安装失败（若提示权限，请以管理员身份运行）")
        raise RuntimeError("msys2 install failed")
    # 3) 配置镜像 + 关闭签名
    _configure_pacman_mirror(msys)
    bash = os.path.join(msys, "usr", "bin", "bash.exe")
    # 4) 同步并安装工具链 + cmake + ffmpeg（中科大镜像）
    log("pacman 同步并安装 gcc/cmake/ffmpeg（中科大镜像，可能需数分钟）…")
    run([bash, "-lc", "pacman -Syu --noconfirm"])
    run([bash, "-lc",
         "pacman -S --noconfirm mingw-w64-x86_64-toolchain "
         "mingw-w64-x86_64-cmake mingw-w64-x86_64-ffmpeg"])
    if not os.path.exists(gcc):
        error("MSYS2 装包后仍未找到 gcc.exe")
        raise RuntimeError("msys2 packages install failed")
    log(f"MSYS2 就绪: {gcc}")
    return msys


# ----------------------------------------------------------------------- #
# 3. .NET 8 SDK（微软官方 CDN，国内可直连）
# ----------------------------------------------------------------------- #
def ensure_dotnet():
    if which("dotnet"):
        log("检测到 dotnet，跳过")
        return
    dotnet_exe = os.path.join(DEPS, "dotnet", "dotnet.exe")
    if os.path.exists(dotnet_exe):
        log("检测到本地 dotnet，跳过")
        return
    if not ON_WIN:
        error(".NET SDK 仅在本机(Windows)下载")
        return
    ps1 = os.path.join(DEPS, "dotnet-install.ps1")
    download_file("https://dot.net/v1/dotnet-install.ps1", ps1, "dotnet-install.ps1", use_mirror=False)
    install_dir = os.path.join(DEPS, "dotnet")
    log("下载并安装 .NET 8 SDK（微软官方 CDN，国内可直连，约 200MB）")
    rc = run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
              "-Command",
              f"& {{ & '{ps1}' -Channel 8.0 -InstallDir '{install_dir}' }}"])
    if rc.returncode != 0:
        error(".NET SDK 安装失败，请检查网络或手动安装 .NET 8 SDK")
    else:
        log(f".NET SDK 就绪: {dotnet_exe}")


def main():
    if not ON_WIN:
        error("本软件及构建脚本仅支持 Windows。请在 Windows 上运行 bootstrap.py / build.py。")
        sys.exit(2)
    log("准备编译环境与依赖（gcc/MinGW 路线，MSYS2 走中科大镜像）……")
    ensure_cmake()
    ensure_msys2()
    ensure_dotnet()
    log("全部依赖就绪！下一步运行:  python build.py")


if __name__ == "__main__":
    main()

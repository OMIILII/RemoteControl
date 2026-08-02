#!/usr/bin/env python3
"""
build.py - 一键编译整个远程控制软件。

前置：先运行  python bootstrap.py  （下载 VS 生成工具 / CMake / ffmpeg / .NET SDK）。
本脚本会：
  1. 用 CMake + MSVC 编译 src/cpp -> rc_core.dll（抓屏/编码/解码/输入核心）
  2. 用 dotnet 发布 src/cs  -> publish/RemoteControl.exe（GUI）
  3. 把 rc_core.dll 和 ffmpeg 运行时 DLL 拷进 publish/，得到可直接运行的目录

仅在 Windows 上运行。
"""

import os
import sys
import glob
import shutil
import subprocess

ROOT = os.path.dirname(os.path.abspath(__file__))
DEPS = os.path.join(ROOT, "deps")
CPP = os.path.join(ROOT, "src", "cpp")
CS = os.path.join(ROOT, "src", "cs")
CS_ADMIN = os.path.join(ROOT, "src", "cs_admin")
BUILD = os.path.join(ROOT, "build")
PUBLISH = os.path.join(ROOT, "publish")
PUBLISH_ADMIN = os.path.join(ROOT, "publish_admin")
MSYS2_MINGW = os.path.join(DEPS, "msys64", "mingw64")
FFMPEG_ROOT = MSYS2_MINGW if os.path.isdir(os.path.join(MSYS2_MINGW, "include", "libavcodec")) else os.path.join(DEPS, "ffmpeg")


def log(m):
    print(f"[build] {m}", flush=True)


def find_file(candidates):
    for c in candidates:
        if c and os.path.exists(c):
            return c
    return None


def main():
    if os.name != "nt":
        print("[build][ERROR] 仅支持 Windows", file=sys.stderr)
        sys.exit(2)

    os.makedirs(BUILD, exist_ok=True)
    os.makedirs(PUBLISH, exist_ok=True)
    os.makedirs(PUBLISH_ADMIN, exist_ok=True)

    # --- locate tools (prefer the ones bootstrap downloaded) -------------
    cmake = find_file([
        os.path.join(DEPS, "cmake", "bin", "cmake.exe"),
        shutil.which("cmake"),
    ])
    dotnet = find_file([
        os.path.join(DEPS, "dotnet", "dotnet.exe"),
        shutil.which("dotnet"),
    ])
    mingw = os.path.join(DEPS, "msys64", "mingw64")
    gcc = find_file([os.path.join(mingw, "bin", "gcc.exe"), shutil.which("gcc")])
    if not cmake:
        print("[build][ERROR] 找不到 cmake，请先运行 bootstrap.py", file=sys.stderr); sys.exit(1)
    if not dotnet:
        print("[build][ERROR] 找不到 dotnet，请先运行 bootstrap.py", file=sys.stderr); sys.exit(1)
    if not gcc:
        print("[build][ERROR] 找不到 MinGW/gcc，请先运行 bootstrap.py", file=sys.stderr); sys.exit(1)
    mingw_bin = os.path.dirname(gcc)
    os.environ["PATH"] = mingw_bin + os.pathsep + os.environ.get("PATH", "")
    if not os.path.exists(os.path.join(FFMPEG_ROOT, "include", "libavcodec", "avcodec.h")):
        print("[build][ERROR] 找不到 ffmpeg，请先运行 bootstrap.py", file=sys.stderr); sys.exit(1)

    log(f"cmake   = {cmake}")
    log(f"dotnet  = {dotnet}")
    log(f"mingw  = {mingw_bin}")
    log(f"ffmpeg  = {FFMPEG_ROOT}")

    # --- 1. compile the C++ core (MinGW) ---------------------------------
    # 注意：CMake 需要 Windows 风格路径（正斜杠即可），不能用 MSYS 的 /d/... 风格，
    # 否则会报 "not a full path to an existing compiler tool"。
    def win(p):
        return p.replace("\\", "/")
    gcc_w = win(os.path.join(mingw_bin, "gcc.exe"))
    gxx_w = win(os.path.join(mingw_bin, "g++.exe"))
    make_w = win(os.path.join(mingw_bin, "mingw32-make.exe"))
    log("配置 CMake (MinGW Makefiles / x64) ...")
    rc = subprocess.run([
        cmake, "-G", "MinGW Makefiles",
        f"-DCMAKE_C_COMPILER={gcc_w}",
        f"-DCMAKE_CXX_COMPILER={gxx_w}",
        f"-DCMAKE_MAKE_PROGRAM={make_w}",
        "-DCMAKE_BUILD_TYPE=Release",
        f"-DFFMPEG_ROOT={win(FFMPEG_ROOT)}",
        "-S", CPP, "-B", BUILD,
    ])
    if rc.returncode != 0:
        print("[build][ERROR] CMake 配置失败", file=sys.stderr); sys.exit(1)

    log("编译 rc_core.dll (Release) ...")
    rc = subprocess.run([cmake, "--build", BUILD, "--config", "Release"])
    if rc.returncode != 0:
        print("[build][ERROR] 编译 rc_core.dll 失败", file=sys.stderr); sys.exit(1)

    dll = find_file(glob.glob(os.path.join(BUILD, "**", "rc_core.dll"), recursive=True))
    if not dll:
        print("[build][ERROR] 找不到编译出的 rc_core.dll", file=sys.stderr); sys.exit(1)
    log(f"rc_core.dll -> {dll}")

    # --- 2. publish the C# GUI -------------------------------------------
    log("发布 C# GUI (self-contained, win-x64) ...")
    rc = subprocess.run([
        dotnet, "publish", CS, "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-o", PUBLISH,
    ])
    if rc.returncode != 0:
        print("[build][ERROR] dotnet publish 失败", file=sys.stderr); sys.exit(1)

    # --- 2b. publish the admin GUI (separate deliverable) ----------------
    # 后台管理端（AdminApp）：纯托管 WinForms，无 native 依赖。
    # 单独发布到 publish_admin/，【不进入客户端安装包】
    # （make_installer.py 只同步 publish/，因此 AdminApp 不会被打进安装包）。
    if os.path.isdir(CS_ADMIN):
        log("发布后台管理端 AdminApp (self-contained, win-x64) -> publish_admin/ ...")
        rc = subprocess.run([
            dotnet, "publish", CS_ADMIN, "-c", "Release", "-r", "win-x64",
            "--self-contained", "true", "-o", PUBLISH_ADMIN,
        ])
        if rc.returncode != 0:
            print("[build][ERROR] AdminApp publish 失败", file=sys.stderr); sys.exit(1)
        # 清理 publish/ 中可能残留的旧 AdminApp 文件（dotnet publish 不会删过期文件）
        for f in glob.glob(os.path.join(PUBLISH, "RemoteControlAdmin.*")):
            try:
                os.remove(f)
            except OSError:
                pass
        log(f"AdminApp -> {os.path.join(PUBLISH_ADMIN, 'RemoteControlAdmin.exe')}（独立于客户端安装包）")

    # --- 3. bundle native DLLs ------------------------------------------
    # 用 ldd 解析 rc_core.dll 的传递依赖，只拷贝它真正需要的 mingw64 DLL
    # （ffmpeg 及其编解码器依赖），避免把整个 mingw64/bin 几百 MB 都搬进来。
    shutil.copyfile(dll, os.path.join(PUBLISH, "rc_core.dll"))
    ldd = os.path.join(mingw_bin, "ldd.exe")
    copied = 0
    if os.path.exists(ldd):
        env = dict(os.environ)
        env["PATH"] = mingw_bin + os.pathsep + env.get("PATH", "")
        out = subprocess.run([ldd, dll], capture_output=True, text=True, env=env).stdout
        seen = set()
        for line in out.splitlines():
            # 形如:  avcodec-62.dll => /d/ai/.../mingw64/bin/avcodec-62.dll (0x...)
            if "=>" not in line:
                continue
            rhs = line.split("=>", 1)[1].strip()
            path = rhs.split(" (")[0].strip()
            low = path.lower().replace("\\", "/")
            if "mingw64/bin" not in low:
                continue  # 跳过系统 DLL (C:/Windows/...)
            # 把 MSYS 风格 /d/... 转成 Windows 盘符路径
            if low.startswith("/") and len(low) > 2 and low[2] == "/":
                path = low[1].upper() + ":" + low[2:]
            name = os.path.basename(path)
            if name in seen or not os.path.exists(path):
                continue
            seen.add(name)
            shutil.copyfile(path, os.path.join(PUBLISH, name))
            copied += 1
    else:
        # 回退：没有 ldd 时，拷贝 ffmpeg bin 下核心 DLL
        bin_dir = os.path.join(FFMPEG_ROOT, "bin")
        if os.path.isdir(bin_dir):
            for f in os.listdir(bin_dir):
                if f.lower().endswith(".dll"):
                    shutil.copyfile(os.path.join(bin_dir, f), os.path.join(PUBLISH, f))
                    copied += 1
    log(f"已拷贝 rc_core.dll + {copied} 个依赖 DLL 到 {PUBLISH}")

    # --- 3b. Optional: write overlay config (Phase 7E) --------------------
    # 如果环境变量 RC_OVERLAY_CONFIG 存在，将其作为 JSON 写入 exe 叠加层。
    # Builder（make_installer.py）会根据实际部署参数自动设此变量。
    overlay_config = os.environ.get("RC_OVERLAY_CONFIG", "")
    if overlay_config:
        overlay_py = os.path.join(ROOT, "src", "py", "overlay_write.py")
        exe = os.path.join(PUBLISH, "RemoteControl.exe")
        if os.path.exists(overlay_py) and os.path.exists(exe):
            dst = os.path.join(PUBLISH, "rm_agent.exe")
            rc = subprocess.run([
                sys.executable, overlay_py, exe, overlay_config, dst
            ])
            if rc.returncode == 0:
                log(f"已写入 overlay config -> {dst}")
            else:
                log("[WARN] overlay config 写入失败")
    else:
        log("(未设置 RC_OVERLAY_CONFIG，跳过 overlay)")

    # --- 4. bundle UCRT (Universal C Runtime) --------------------------
    # .NET self-contained 不带 UCRT；Win10 1809+ 自带，Win7/8/精简 Win10 缺。
    # 缺了会报 "无法启动此程序，因为计算机中丢失 api-ms-win-crt-runtime-l1-1-0.dll"。
    # 从 C:\Windows\System32\downlevel\ 拷贝应用本地转发 DLL，
    # 这套 DLL 是微软专为旧版 Windows 应用本地部署留的。
    downlevel = r"C:\Windows\System32\downlevel"
    for dest in (PUBLISH, PUBLISH_ADMIN):
        if os.path.isdir(downlevel):
            ucrt_copied = 0
            for f in os.listdir(downlevel):
                low = f.lower()
                if low.startswith("api-ms-win-crt-") or low == "ucrtbase.dll":
                    shutil.copyfile(os.path.join(downlevel, f), os.path.join(dest, f))
                    ucrt_copied += 1
            log(f"已拷贝 {ucrt_copied} 个 UCRT 转发 DLL 到 {dest}")
        else:
            log(f"[WARN] 找不到 C:\\Windows\\System32\\downlevel，跳过 {dest} 的 UCRT 拷贝")

    log("==================================================")
    log("构建完成！")
    log(f"  运行被控端 / 控制端: {os.path.join(PUBLISH, 'RemoteControl.exe')}")
    log("  先在 A 机器跑信令服务器: python src/py/signaling_server.py")
    log("  再在两台机器上分别打开 RemoteControl.exe，选角色并填同一个房间号。")
    log("==================================================")


if __name__ == "__main__":
    main()

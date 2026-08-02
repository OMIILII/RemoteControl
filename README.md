# RemoteControl — Windows 远程控制

基于 GPU 抓屏 + H.264 视频流，延迟对标 TeamViewer / AnyDesk（理论上）。

## 原理

不是逐张截图发送，而是**持续 H.264 视频流**：

- **DXGI Desktop Duplication** — GPU 直接抓帧缓冲区，画面变化时才返回，自带脏矩形
- **x264 编码** — `ultrafast` + `zerolatency`，IDR 关键帧 + P 帧连续推送
- **单 TCP 长连接** — 视频流、输入事件、控制信令走同一条连接

## 技术栈

| 层 | 语言 | 说明 |
|---|---|---|
| 抓屏 / 编解码 | C++ (rc_core.dll) | DXGI + libx264 + SDL2 渲染 |
| 输入注入 | C++ | SendInput 键盘 / 鼠标 / 滚轮 |
| 界面 | C# WinForms | 被控端 + 控制端 P/Invoke 调用 C++ |

## 目录

```
├── build.py             一键编译
├── bootstrap.py          环境初始化（自动下载 MSVC / CMake / FFmpeg / .NET 8 SDK）
├── packaging/            NSIS 安装包脚本
├── src/
│   ├── cpp/              C++ 核心 → rc_core.dll
│   ├── cs/               C# WinForms 客户端
│   └── cs_agent/         被控端 Agent（静默部署，无窗口）
```

## 构建

仅 Windows（依赖 DXGI + MSVC）。

```powershell
python bootstrap.py    # 首次：自动下载依赖到 deps/
python build.py        # 编译 C++ + 发布 .NET
```

## 运行

需要一个 TCP 中继服务器。中继负责配对被控端与控制端，透明转发 TCP 流量。

**被控端**：RemoteControl.exe → 被控端 → 填中继地址和房间号。

**控制端**：RemoteControl.exe → 控制端 → 同一中继、同一房间号 → 开始远程控制。

控制端窗口内鼠标、键盘、滚轮实时同步到被控端。

## Agent（部署端）

`RemoteControlAgent.exe` 为单文件可执行程序，可部署到被控端机器：

```
RemoteControlAgent.exe              # 普通模式运行
RemoteControlAgent.exe --install    # 安装为 Windows 服务（SYSTEM 看门狗）
RemoteControlAgent.exe --uninstall  # 卸载服务
```

服务模式使用 `WTSQueryUserToken` + `CreateProcessAsUser` 将 Agent 注入用户会话，崩溃自动重启。

## 协议

二进制帧，长度前缀：

```
[byte type][int32 length LE][payload]
```

| type | 含义 | payload |
|---|---|---|
| 1 | Hello | 无 |
| 2 | VideoConfig | `[int32 w][int32 h][int32 fps][int32 extra_size][extradata]` |
| 3 | VideoFrame | `[byte is_key][int32 size][H.264 NAL...]` |
| 4 | InputEvent | `[byte kind][...]` — Move / Button / Wheel / Key |
| 5 | Bye | 无 |
| 6 | Ping | 无 |

中继只做配对转发，不解析 payload。

## License

MIT

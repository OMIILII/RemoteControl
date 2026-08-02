[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

# RemoteControl — Windows 远程控制

一句话概览
- 基于 GPU 抓屏 + libx264 实时编码，通过单条 TCP 连接承载视频与输入事件，面向低延迟远程桌面控制。

特性
- DXGI Desktop Duplication：GPU 抓屏，只在画面变化时返回并提供脏矩形信息。
- x264（ultrafast + zerolatency）：面向实时传输，IDR + P 帧连续推送。
- 单 TCP 长连接：视频、输入、控制信令共享一条连接，便于中继配对和转发。
- Agent 服务模式：可安装为 Windows 服务，使用 WTSQueryUserToken + CreateProcessAsUser 注入用户会话并自动重启。

快速开始（端到端）
- 环境要求：
  - Windows 10/11 x64
  - Python 3.8+
  - Visual Studio / MSVC（建议 VS2022）
  - CMake >= 3.20
  - .NET 8.x SDK
  - libx264、SDL2、FFmpeg（测试）
- 克隆：
  - git clone https://github.com/OMIILII/RemoteControl.git
- 初始化与构建：
  - python bootstrap.py
  - python build.py
- 最小中继示例（仅演示）见 PROTOCOL.md 中的示例代码。
- 运行：
  - Agent: RemoteControlAgent.exe [--install | --uninstall]
  - GUI: RemoteControl.exe — 选择控制端或被控端，输入中继地址与房间号。

安全（强烈建议）
- 示例中继默认不加密、无鉴权。生产部署必须至少启用 TLS 或在受信网络中运行，并使用配对 token/一次性密码进行鉴权。
- Agent 服务模式有较高权限风险，仅在受信环境安装，并记录安装/连接审计日志。

性能与带宽
- 1080p@30fps 下典型码率约 1–6 Mbps，取决于桌面活动。
- 减少带宽的方法：降低分辨率/帧率、调整 x264 参数或仅编码脏矩形。

故障排查
- 黑屏或花屏：确认 SPS/PPS（extradata）是否正确传递，确认 NAL 封装格式一致。
- 延迟高：检查网络丢包与带宽，降低 fps 或分辨率。
- 配对失败：确认中继运行、防火墙设置、房间号一致。

贡献
- 欢迎 Issue/PR。协议或不兼容变更请先开 Issue 讨论。提交 PR 时请包含测试步骤（Windows 实机优先）。

许可证与维护者
- 本项目使用 MIT 许可证，详情见根目录的 LICENSE 文件。
- 维护者：OMIILII

[![build status](https://img.shields.io/badge/build-unknown-lightgrey)]() [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

# RemoteControl — Windows Remote Control

One-line summary
- GPU capture + libx264 realtime encoding over a single TCP connection carrying both video and input events for low-latency remote desktop control.

Features
- DXGI Desktop Duplication: GPU-level capture, returns frames only when the desktop changes and provides dirty-rect info.
- x264 (ultrafast + zerolatency): tuned for real-time streaming (IDR + P-frame flow).
- Single TCP connection: video, input events, and control signaling share one connection for simpler relay/pairing.
- Agent service mode: optional Windows service that uses WTSQueryUserToken + CreateProcessAsUser to inject into user sessions and auto-restarts on crash.

Quick Start (end-to-end)
- Requirements (Windows x64):
  - Windows 10/11 x64
  - Python 3.8+ (bootstrap/build scripts)
  - Visual Studio / MSVC (e.g., VS 2022)
  - CMake >= 3.20
  - .NET 8.x SDK
  - libx264, SDL2, FFmpeg (for testing)
- Clone:
  - git clone https://github.com/OMIILII/RemoteControl.git
- Prepare and build:
  - python bootstrap.py    # download deps into deps/
  - python build.py        # build C++ core and publish .NET clients
- Minimal relay (demo only; not production):
  - A tiny relay can pair two clients and transparently forward TCP (see PROTOCOL.md for protocol details).
- Run:
  - Agent: RemoteControlAgent.exe [--install | --uninstall]
  - GUI: RemoteControl.exe — choose Controller or Host, enter relay host:port and room id.

See PROTOCOL.md for the exact binary protocol, message types, and parsing guidance.

Security (must read)
- The default relay in examples is unauthenticated and unencrypted. For production:
  - Use TLS on the relay, or run relay inside a VPN.
  - Add per-room tokens or one-time pairing codes for authentication.
  - Limit Agent installation to trusted administrators (service mode runs as SYSTEM).
  - Log install/uninstall, connections, and suspicious events for audit.

Performance & bandwidth
- Typical 1080p@30fps with ultrafast/zerolatency: ~1–6 Mbps depending on content.
- To reduce bandwidth: lower resolution, lower FPS, or adjust x264 rate/QP settings.

Troubleshooting
- Black/garbled video: check SPS/PPS extradata and agreed H.264 encapsulation (Annex-B vs AVCC).
- High latency: check network bandwidth/packet loss, reduce fps/resolution.
- Pairing fails: check relay status, firewall/NAT, and that both endpoints use the same room string.

Contributing
- Open issues or PRs. For protocol or compatibility changes, open an issue first.
- Please include testing notes (Windows environment) and minimal reproduction steps.

License & Maintainers
- This project is licensed under the MIT License — see the LICENSE file for details.
- Maintainer: OMIILII

# Protocol — Binary framing and messages

Overview
- Simple length-prefixed binary messages over a single TCP stream.
- All multi-byte integers are little-endian (LE).
- Each message:
  [1 byte type][4 bytes length LE][payload (length bytes)]

Message types
- 0x01 Hello
  - length = 0
- 0x02 VideoConfig
  - payload:
    - int32 w
    - int32 h
    - int32 fps
    - int32 extra_size
    - extra_size bytes extradata (e.g., SPS/PPS)
- 0x03 VideoFrame
  - payload:
    - uint8 is_key (0x01 = keyframe / IDR, 0x00 = non-key)
    - int32 size  (bytes)
    - size bytes H.264 data (NAL units; Annex-B or AVCC must be agreed)
- 0x04 InputEvent
  - payload:
    - uint8 kind (1=MouseMove,2=MouseButton,3=Wheel,4=Key)
    - MouseMove (kind=1): int32 x, int32 y, int32 flags
    - MouseButton (kind=2): uint8 button, uint8 action
    - Wheel (kind=3): int32 delta, uint8 orientation
    - Key (kind=4): int32 vk_code, uint8 action, int32 modifiers
- 0x05 Bye
  - length = 0
- 0x06 Ping
  - length = 0

Parsing guidance
- Read 1 byte for type, then 4 bytes for length (LE).
- Loop-read until the specified payload length is obtained (payload may arrive fragmented).
- For VideoFrame, after reading size, loop-read size bytes for the H.264 payload.
- Implement payload size limits (suggested max 10–20 MB; hard cap 100 MB). Reject and close on oversized values.

H.264 packaging
- Agree on one of:
  - Annex-B (start code 0x00 00 00 01 before each NAL)
  - AVCC (length-prefixed NALs) with extradata containing SPS/PPS in AVCC format
- If using Annex-B, VideoConfig extradata may be empty; otherwise extradata should carry SPS/PPS to initialize decoder.

Heartbeat & timeouts
- Ping message (0x06) can be used as heartbeat.
- Suggested policy:
  - Send ping every 10–30s.
  - If no data or ping response for N * interval (e.g., 3 times), consider connection dead and reconnect.

Auth extension (optional)
- Option A: Hello carries token
  - Hello payload: [int16 auth_len][auth_len bytes token] (auth_len=0 => no auth)
- Option B: New message type 0x07 Auth
  - payload: [int32 token_size][token_bytes]
- Relay should validate token before pairing; if invalid, reply Bye and close.

Error handling & security
- On malformed messages (e.g., length mismatch), log and close connection.
- Defend against large allocations by enforcing maximum allowed payload size.
- Relay must not parse H.264 payload for routing; keep relay logic simple (pair & forward) but enforce auth/TLS in production.

Binary examples (hex)
- Hello:
  - 01 00 00 00 00
- VideoConfig (w=1920,h=1080,fps=30,extra_size=4,extradata=DE AD BE EF)
  - 02 10 00 00 00 80 07 00 00 38 04 00 00 04 00 00 00 DE AD BE EF
  - (interpretation: type=0x02, length=16)
- VideoFrame (is_key=1,size=0x00000100,...) — illustrative only:
  - 03 01 00 01 00 00 01 00 00 01 ...

Best practices
- Always transmit VideoConfig before sending VideoFrame messages.
- Use sequence or timestamping at application level if you need frame reordering/dedup logic later.
- Consider adding a version byte to Hello or a dedicated header if you plan breaking changes.

Notes
- This document is intended to be a precise developer reference for implementing encoders/decoders and client code that interoperate over the relay.

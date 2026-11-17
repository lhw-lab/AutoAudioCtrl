# AudioCtrl Stand Protocol v2

The stand and the Windows host communicate with fixed 8-byte frames over one of two
transports (same payload everywhere — only the wrapping differs):

- **USB HID** (original STM32F103 firmware) — described below.
- **USB CDC serial** (ESP32-C3 firmware v2.1+) — see [Serial transport](#serial-transport-esp32-c3).

The report descriptor declares one 8-byte Input report and one 8-byte Output report
(no report IDs; Windows prepends a zero byte on the wire, so endpoint packets are
9 bytes and the max packet size is 16).

Device: VID `0x0483`, PID `0x572B` (ST default — replace with your own VID for products).

## Frame format

**Uplink (stand → PC), Input report:**

| Byte | EVENT (0x01) | INFO (0x04) |
|---|---|---|
| 0 | message type | message type |
| 1 | sequence number (1–255, wraps) | 0x00 |
| 2 | event: `0x01` = headphones lifted, `0x02` = headphones placed | firmware version ×16 (0x20 = v2.0) |
| 3–4 | stand ID, little-endian | stand ID, little-endian |
| 5–7 | reserved | 5: sensor state (`1` = lifted); 6–7 reserved |

**Downlink (PC → stand), Output report:**

| Byte | ACK (0x01) | QUERY (0x03) |
|---|---|---|
| 0 | message type | message type |
| 1 | sequence number being acknowledged | — |
| 2 | result code (below) | — |
| 3–7 | reserved | reserved |

ACK result codes: `0x00` switched OK · `0x01` target device missing · `0x02` no mapping configured · `0x03` host error.

## Semantics

- **Stand ID** — FNV-1a hash of the STM32 96-bit UID (`0x1FFFF7E8`), folded to 16 bits. Stable across reboots and reflashes; distinguishes multiple stands.
- **Lifted / placed** — active-low sensor on PB0: pressed (headphones resting on the stand) = "placed", released = "lifted". Raw level is debounced with a 30 ms stable-count filter; every confirmed transition increments the sequence and queues an EVENT.
- **Reliability** — the firmware retransmits an unacknowledged EVENT every 300 ms, up to 4 attempts, then gives up (the host is treated as offline). The host de-duplicates by sequence number and re-ACKs duplicates with the original result code.
- **No heartbeat** — purely event-driven. The stand talks on power-up, on sensor transitions, and when answering a QUERY.
- **QUERY** — sent by the host when a stand connects (also after hot-plug and system resume). The stand answers with INFO carrying its ID, firmware version, and current sensor state, which re-syncs state after any downtime.
- **Watchdog** — IWDG at ~2.4 s, fed every main-loop pass (~1 ms). A hang reboots the stand within ~2 s; USB re-enumerates and the host re-syncs via QUERY.

## Wire example

`01 25 02 05 FF 00 00 00` — EVENT, seq 0x25, `02` = placed, stand ID `0xFF05`.
Host replies `01 25 00 00 00 00 00 00` — ACK seq 0x25, result 0 (OK).

## Serial transport (ESP32-C3)

The v2 hardware platform (ESP32-C3 SuperMini) carries the **same 8-byte frames**
over USB CDC instead of HID. The C3's USB Serial/JTAG peripheral is a fixed-function
device: it cannot do HID, but needs no driver on Win10/11 (inbox usbser.sys) and
supports buttonless esptool flashing via DTR/RTS.

**Frame (fixed 11 bytes):**

```
A5 5A | <8-byte v2 payload> | CRC8
```

- CRC8: polynomial `0x07`, init `0x00`, MSB-first, computed over the 8 payload bytes.
- The byte stream resynchronizes on the magic `A5 5A`; non-frame bytes (boot logs,
  noise) are discarded by the parser on both ends.
- Everything else — EVENT/ACK/QUERY semantics, seq de-dup, 300 ms × 4 retransmit,
  stand ID field — is identical to HID. Stand ID on C3 = FNV-1a of the eFuse MAC,
  folded to 16 bits (same algorithm as the STM32 UID hash).

**USB identity:** VID `0x303A` (Espressif), PID `0x1001` — fixed by the peripheral,
not changeable. The host therefore pre-filters candidate COM ports by VID and then
confirms "our firmware" by protocol handshake (open port → send QUERY → expect INFO
within ~1.5 s; anything else gets closed silently).

**Serial-only additions (downlink type `0x05`, TEST):**

- `05 01 00 00 00 00 00 00` — bench-test toggle: flips the firmware's sensor-test
  bit, toggles the sensor state once (single flip — note: the physical BOOT button is a stateful dual trigger source since fw v2.3, but the remote TEST command keeps single-flip semantics for bench scripts). Used by automated
  hardware-in-the-loop tests and the app's "simulate lift/place" menu item.
  The old STM32 firmware ignores this frame type.
- INFO banner: on CDC connect the firmware sends an unsolicited INFO, and repeats
  it every 2 s until the first downlink frame arrives (robust discovery after any
  reset, independent of DTR edge detection).
- ACK echo (diagnostic, `ECHO_ON_ACK` firmware option, on by default in v2.1):
  the firmware answers an accepted ACK with an immediate INFO — lets the host
  measure round-trip latency and confirm downlink delivery.

**Host write requirement:** after writing a frame the host must flush
(`FlushFileBuffers`) — usbser.sys buffers small writes and an unflushed ACK loses
the race against the firmware's 300 ms retransmit window (measured: with flush the
ack round-trip is ~1 ms).

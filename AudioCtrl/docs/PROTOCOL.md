# AudioCtrl Stand Protocol v2

The stand and the Windows host communicate over USB HID with fixed 8-byte frames.
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

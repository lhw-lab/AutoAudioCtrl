# Architecture Notes

A quick map for anyone digging into the code. (Code comments are in Chinese; this file gives you the English orientation.)

## System overview

Three cooperating parts:

- **`stm32/firmware/`** — STM32F103C8T6 firmware (archived STM32CubeIDE project,
  legacy "engineer tier"). Polls the sensor switch with 30 ms debouncing, speaks the
  8-byte protocol over USB HID ([PROTOCOL.md](PROTOCOL.md)), retransmits events
  until ACKed, feeds an IWDG watchdog.
- **`firmware-c3/`** — ESP32-C3 SuperMini firmware (PlatformIO + Arduino core 2.x).
  Same 8-byte payload over USB CDC (11-byte framed, see PROTOCOL), stand ID from
  the eFuse MAC, esp_task_wdt, bench-test commands (remote TEST toggle).
- **`AudioCtrl/`** — Windows tray app (.NET 10, WPF, **zero NuGet dependencies**).
  Discovers stands over two transports simultaneously, decodes frames, schedules
  device switches, exposes a single-page UI.

## Host app layers

```
StandTransportHub     aggregates transports, routes downlink frames by devicePath
        ├── StandHidService     (IStandTransport) USB HID, VID 0483:572B
        │     HidDeviceEnumerator   SetupAPI enumeration (SetupDiGetClassDevs +
        │                           HID interface GUID, VID/PID from device path)
        │     one connection per stand: overlapped read thread + sync write handle
        └── StandSerialService  (IStandTransport) USB CDC serial, VID 303A:1001
              ComPortEnumerator    VID-filtered COM discovery (SERIALCOMM ∩
                                   Enum\USB registry views, both read-only)
              SerialFrameCodec     A5 5A + payload + CRC8 wrap / stream resync
              per-port connection: comm-timeout blocking read thread, sync write
              + FlushFileBuffers, 1.5 s QUERY probe to confirm "our firmware"
        │ 8-byte payload frames (identical for both transports)
HidProtocol           frame build/parse (pure functions, shared by both transports)
        │
StandScheduler        the brain: seq de-dup, ACK result codes, "last lifted wins",
                      all-placed → fallback device (transport-agnostic)
        │
StandSettingsService  per-stand bindings persisted to Config/stand-settings.json
        │
AudioDeviceService    Core Audio COM interop: enumerate devices, set default via
                      PolicyConfig (undocumented COM, same trick as SoundSwitch),
                      IMMNotificationClient callbacks

EspToolFlasher         one-click flashing for new C3 boards (separate concern from
                       transports): resolves bundled/dev esptool + firmware images,
                       runs the write as a child process, parses progress. The Flash
                       dialog coordinates with StandSerialService (release port,
                       suppress reconnects, rescan after the board auto-resets)
```

A C3 serial stand and an STM32 HID stand can be plugged in at the same time; the
scheduler keys everything by stand ID (16-bit hash) and never looks at devicePath
format. `StandRuntime.TransportKind` (set from the path on connect, remembered while offline) is
display-only.

UI: `MainWindow` (single page: device cards + right-click binding menus + mapping list + stand status), `TrayIconService` (raw `Shell_NotifyIcon` + WPF dark context menu), `SwitchToastWindow` (queued toasts), `AppMessageBox` (themed dialogs), `ThemeService` (light/dark/system), `LanguageService` + `Loc` (zh-CN / en-US language packs swapped as merged resource dictionaries).

Everything is Win32-clean: tray, HID enumeration, hot-plug (`WM_DEVICECHANGE`) and power-resume (`WM_POWERBROADCAST`) all go through window messages / P-Invoke. State is event-driven; there is no polling loop on the host.

## Hard-won rules (do not break)

1. **Never re-issue `ReadFile` while a previous overlapped read is still pending on the same `OVERLAPPED`.** Wait for it instead — reusing an OVERLAPPED with pending I/O is undefined behavior and caused both "duplicate frame storms" and a silent read-path deadlock that dropped all events. (The manual-reset event must also be `ResetEvent`-ed after every `GetOverlappedResult`.)
2. **PolicyConfig**: use the single CoClass CLSID `870AF99C-171D-4F9E-AF0D-E63DF40C2BC9` and cast (`as`) to `IPolicyConfigX` / `IPolicyConfig` / `IPolicyConfigVista`. Interfaces must declare the full 12-method vtable with `[PreserveSig]` or you get `0x8007065E`.
3. **WPF resource lookup only walks up the tree** — styles defined in `Border.Resources` are invisible to `Window.FindResource`. Shared styles live in `Window.Resources`.
4. **WPF MenuItem toggles `IsChecked` before raising `Click`** — don't invert it again in the handler.
5. Store stand bindings by **device ID**, never by name (Windows shows duplicate names for the same physical endpoint, e.g. a headset's stereo + hands-free profiles; also only bind `IsActive == true` endpoints).
6. Firmware custom code lives strictly inside `/* USER CODE BEGIN/END */` blocks so CubeMX regeneration doesn't eat it.
7. **Serial writes must `FlushFileBuffers`** — usbser.sys buffers small writes and does not push them onto the USB wire promptly. An unflushed ACK arrives too late against the firmware's 300 ms retransmit window and produces spurious retransmits (with flush the measured round-trip is ~1 ms). Applies to every `WriteFile` on a COM port in this codebase.
8. **Never write to a CDC port the firmware side may consider disconnected** (C3 HWCDC `write` can block indefinitely when the host is gone, starving the watchdog). The C3 firmware guards with `(bool)Serial` and drops frames instead — events are covered by retransmit, so dropping is the correct v1-compatible behavior. On the host side, expect one OS-buffered retransmit race at worst; the scheduler's seq de-dup makes it harmless.
9. **Don't discover serial stands by opening arbitrary COM ports.** Pre-filter by USB VID (Espressif `0x303A`) via the two registry views (`SERIALCOMM` ∩ `Enum\USB`), then protocol-probe with QUERY (1.5 s timeout). Opening unknown ports (Bluetooth modems, other people's Arduinos) has side effects.

## Debugging tricks that paid off

- Rolling file logs next to the exe (`Logs/audioctrl-YYYYMMDD.log`) — Release builds have no console.
- Live firmware state via ST-Link without stopping the MCU:
  `STM32_Programmer_CLI -c port=SWD mode=HOTPLUG -r <addr> <len> <file>` with addresses from the `.map` file. This once proved the firmware innocent (its `tx_seq` was 106 while the host log stalled at 44) and pinned a host-side read bug in minutes.
- Simulating tray/hotplug/power events by `PostMessage`-ing `WM_APP` callbacks, `WM_DEVICECHANGE` and `WM_POWERBROADCAST` into the live window — makes those paths automatable.

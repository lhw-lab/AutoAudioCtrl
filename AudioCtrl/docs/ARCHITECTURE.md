# Architecture Notes

A quick map for anyone digging into the code. (Code comments are in Chinese; this file gives you the English orientation.)

## System overview

Two cooperating parts:

- **`firmware/`** — STM32F103C8T6 firmware (STM32CubeIDE project). Polls the sensor switch with 30 ms debouncing, speaks the 8-byte HID protocol ([PROTOCOL.md](PROTOCOL.md)), retransmits events until ACKed, feeds an IWDG watchdog.
- **`AudioCtrl/`** — Windows tray app (.NET 10, WPF, **zero NuGet dependencies**). Discovers stands, decodes frames, schedules device switches, exposes a single-page UI.

## Host app layers

```
HidDeviceEnumerator   SetupAPI enumeration (SetupDiGetClassDevs + HID interface GUID,
                      VID/PID parsed from device path)
        │
StandHidService       one connection per stand: overlapped read thread + sync write
                      handle; hot-plug diff via RefreshConnections()
        │ 8-byte frames
HidProtocol           frame build/parse (pure functions)
        │
StandScheduler        the brain: seq de-dup, ACK result codes, "last lifted wins",
                      all-placed → fallback device
        │
StandSettingsService  per-stand bindings persisted to Config/stand-settings.json
        │
AudioDeviceService    Core Audio COM interop: enumerate devices, set default via
                      PolicyConfig (undocumented COM, same trick as SoundSwitch),
                      IMMNotificationClient callbacks
```

UI: `MainWindow` (single page: device cards + right-click binding menus + mapping list + stand status), `TrayIconService` (raw `Shell_NotifyIcon` + WPF dark context menu), `SwitchToastWindow` (queued toasts), `AppMessageBox` (themed dialogs), `ThemeService` (light/dark/system), `LanguageService` + `Loc` (zh-CN / en-US language packs swapped as merged resource dictionaries).

Everything is Win32-clean: tray, HID enumeration, hot-plug (`WM_DEVICECHANGE`) and power-resume (`WM_POWERBROADCAST`) all go through window messages / P-Invoke. State is event-driven; there is no polling loop on the host.

## Hard-won rules (do not break)

1. **Never re-issue `ReadFile` while a previous overlapped read is still pending on the same `OVERLAPPED`.** Wait for it instead — reusing an OVERLAPPED with pending I/O is undefined behavior and caused both "duplicate frame storms" and a silent read-path deadlock that dropped all events. (The manual-reset event must also be `ResetEvent`-ed after every `GetOverlappedResult`.)
2. **PolicyConfig**: use the single CoClass CLSID `870AF99C-171D-4F9E-AF0D-E63DF40C2BC9` and cast (`as`) to `IPolicyConfigX` / `IPolicyConfig` / `IPolicyConfigVista`. Interfaces must declare the full 12-method vtable with `[PreserveSig]` or you get `0x8007065E`.
3. **WPF resource lookup only walks up the tree** — styles defined in `Border.Resources` are invisible to `Window.FindResource`. Shared styles live in `Window.Resources`.
4. **WPF MenuItem toggles `IsChecked` before raising `Click`** — don't invert it again in the handler.
5. Store stand bindings by **device ID**, never by name (Windows shows duplicate names for the same physical endpoint, e.g. a headset's stereo + hands-free profiles; also only bind `IsActive == true` endpoints).
6. Firmware custom code lives strictly inside `/* USER CODE BEGIN/END */` blocks so CubeMX regeneration doesn't eat it.

## Debugging tricks that paid off

- Rolling file logs next to the exe (`Logs/audioctrl-YYYYMMDD.log`) — Release builds have no console.
- Live firmware state via ST-Link without stopping the MCU:
  `STM32_Programmer_CLI -c port=SWD mode=HOTPLUG -r <addr> <len> <file>` with addresses from the `.map` file. This once proved the firmware innocent (its `tx_seq` was 106 while the host log stalled at 44) and pinned a host-side read bug in minutes.
- Simulating tray/hotplug/power events by `PostMessage`-ing `WM_APP` callbacks, `WM_DEVICECHANGE` and `WM_POWERBROADCAST` into the live window — makes those paths automatable.

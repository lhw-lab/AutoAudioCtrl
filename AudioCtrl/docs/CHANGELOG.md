# Changelog

Public-facing version history.

## v0.17.0 — 2026-11-15 — v2 hardware platform (ESP32-C3, USB serial)

- **Renamed**: 自动音频控制 / Auto Audio Control → **架上知音 / PickUp Audio**
- New `hardware/` directory: 3D-printable stand models (STL) + photos + assembly
  guide — the reference enclosure is now part of the repo
  (exe filename, namespace, and config paths unchanged — upgrades are seamless)
- Fixed capture-only device mappings: a stand can now switch a recording device
  on lift, or an all-placed fallback microphone, without also configuring a
  playback device (previously the scheduler ignored capture-only mappings)
- Default-device no-op skip now requires Console, Multimedia and Communications
  to all point at the target; per-role default differences are corrected
- Serial unplug no longer blocks the UI thread: port close moved outside the
  service lock and runs off the UI thread; disconnect notification fires once
- Host writes check the actual byte count (serial + HID); turning the toast
  toggle off closes the currently visible toast; esptool's `python -m esptool`
  fallback is validated before the flash button is enabled
- Added host unit tests (HID protocol, serial framing, scheduler) to CI; version
  is single-sourced from `Directory.Build.props`; release packaging cleans stale
  runtime artifacts and validates the requested version
- Docs: documented the GPIO9 BOOT strapping constraint for future solder-free
  enclosures (holding BOOT low at power-up enters the ROM downloader)
- New stand transport: USB CDC serial alongside USB HID. `IStandTransport`
  abstraction with two implementations (`StandHidService` kept as-is, new
  `StandSerialService`) aggregated by `StandTransportHub` — old STM32 HID
  stands and new C3 serial stands work simultaneously, bindings and scheduler
  unchanged (same 8-byte protocol payload, see PROTOCOL.md "Serial transport")
- Serial discovery without COM-number coupling: VID-prefiltered enumeration
  (Espressif `0x303A`, read-only registry views) + protocol handshake probe
  (QUERY → INFO within 1.5 s); non-stand ports are closed silently, Bluetooth
  and modem ports are never opened
- Host serial writes flush after every frame (`FlushFileBuffers`) — usbser.sys
  buffers small writes and an unflushed ACK loses the 300 ms retransmit race
  (measured round-trip with flush: ~1 ms)
- Stand cards show transport type (HID / USB Serial); serial stands (C3
  firmware ≥ v2.1) get a right-click "simulate lift/place" bench-test command
- New firmware `firmware-c3/` (PlatformIO + Arduino core): same protocol
  payload over 11-byte framed serial (magic + payload + CRC8), stand ID from
  eFuse MAC, 30 ms debounce, 300 ms × 4 retransmit, task watchdog, periodic
  banner until first host frame, remote TEST command + ACK echo for
  hardware-in-the-loop testing (`firmware-c3/tools/`: probe + automated test
  scripts, 11/11 checks passing on real hardware)
- One-click in-app flashing (new C3 boards): "刷固件" opens a dialog that
  releases the port, runs bundled esptool (release: `Tools\esptool.exe`;
  dev fallbacks: PlatformIO's bundled python+esptool — no system Python
  needed — then pip-installed `python -m esptool` + PlatformIO build dir),
  writes the three C3 images (bootloader 0x0 / partitions 0x8000 / app
  0x10000) with live progress, then the board auto-resets and is re-recognized
  as a stand. Unrecognized Espressif ports (fresh boards) surface as
  preselected candidates. esptool is GPLv2+ invoked as a separate process
  (mere aggregation), registered in THIRD-PARTY-NOTICES alongside the Arduino-ESP32 core (LGPL-2.1)
- C3 firmware v2.3 — **dual trigger sources**: the micro switch (GPIO0, with
  GPIO2 driven low as the ground side) and the on-board BOOT key (GPIO9) are
  equivalent sensors — whichever key last changed takes over as the trigger,
  so an unsoldered board works via BOOT alone and touching the switch later
  switches back. Both keys share semantics: pressed = placed, released = lifted
- **Delete a stand** (right-click its card): removes the stand and all of its
  device mappings; warns when the stand is still plugged in (it would
  reappear on its next event)
- Scheduler: stands without mappings are **skipped instead of blocking** —
  previously one unmapped lifted stand could swallow every other stand's
  place-to-fallback switch
- Toast improvements: playback + recording switches of the same action merge
  into a single notification (250 ms merge window); new tray toggle
  "切换通知" silences all notifications (persisted)
- Fixed a toast storm: re-setting an endpoint that is already the default
  still fires Windows default-change callbacks (×3 roles) — the scheduler now
  skips no-op sets and the ViewModel only toasts on actual device changes
- INFO frames from C3's ACK-echo are logged at DEBUG level ("上线" reserved
  for genuine connections)
- Fixed a transport-label regression: unplugging a serial stand could flip its
  card back to "HID"; `StandRuntime.TransportKind` now remembers the last
  connected transport and stays correct while offline
- Real-hardware regression: C3 serial + STM32 HID stands online together
  (last-lifted-wins, single place-back fallback, all-placed fallback all passed),
  115-press stress test with zero sequence gaps, and the STM32 tier clean under
  the v2 host

## v0.16.1 — 2026-11-07

Closeout round from a second external review (7.3 → target 8.0):

- Docs/images final state (went through several iterations, settled by the author):
  the repo keeps **only the author's own real-device photos** (dev board,
  board with the switch soldered, stand with headphones placed/lifted —
  physically rotated portrait, all EXIF stripped); wiring stays as a text table;
  no UI screenshot / diagram images by author's decision
- Logs now follow the same read-only-dir fallback as config (new shared
  `AppPaths` resolver — exe folder when writable, else `%LOCALAPPDATA%\AudioCtrl`)
- Auto-start registry refresh moved after the single-instance check: launching a
  second copy from a USB stick can no longer hijack the auto-start path
- INFO handling: realigns audio on **any** state mismatch (previously only
  "already lifted"; now "was lifted, actually placed" — e.g. headphones put back
  during sleep — also falls back to speakers). De-dup state (`LastSeq`) is only
  cleared on a genuine (re)connect, not on every QUERY/INFO
- Event-history de-dup dictionary moved onto the UI thread (multi-stand read
  threads could race on it)
- Firmware: `USBD_SELF_POWERED` overridden to 0 (matches the corrected 0x80
  bus-powered descriptor; the GET_STATUS self-powered bit was inconsistent)
- Corrupt settings files are backed up as `.corrupt` instead of being silently
  overwritten by defaults; `File.Replace` falls back to overwrite-move on
  filesystems that don't support it
- UI-exception swallowing now has a flood guard (>30/min → notify & crash
  instead of endlessly logging a half-broken UI)
- Removed leftovers: `SwitchToSettingsTab()`, persisted `StandMapping.DisplayName`
- Added CI (host-app build), `CONTRIBUTING.md`, language switcher links in READMEs

## v0.16.0 — 2026-11-07

Robustness & product-behavior round (from an external code review):

- **Global exception handlers** (UI thread / non-UI / unobserved tasks): tray apps must
  not die silently; exceptions are logged with full stack traces and the app keeps running
- **Logger now records exception type + message + stack** (incl. inner exceptions) —
  the file log is the only diagnostics channel in Release builds
- Fixed a real crash caught by the new handler on first run: the second instance
  released a mutex it never owned (`ReleaseMutex` from a non-owner thread)
- **Atomic config writes** (temp file + `File.Replace`): a crash mid-write can no longer
  corrupt settings JSON (which used to silently reset all mappings)
- Config directory falls back to `%LOCALAPPDATA%\AudioCtrl\` when the exe folder is
  read-only (e.g. the portable app unpacked into Program Files)
- **State alignment on connect**: if the headphone is already lifted when the app starts
  or a stand reconnects, audio switches to that stand's bound device immediately
  (on-stand states are left alone — the app never hijacks your current output)
- Stand `LastSeq` de-dup state is cleared on INFO so a firmware reboot's first event
  can't be swallowed as a "retransmission"
- Silent start: the auto-start entry now passes `--startup` — boots straight to the tray
  without showing the window
- Disconnected device cards are no longer clickable (Windows silently rejects setting
  a non-active endpoint as default)
- Removed ~700 more lines of dormant code: `SettingsViewModel` (hidden settings page),
  hotkey/cycle/HID-mapping settings fields, unused Tab styles — `AppSettings` now holds
  only Theme / Language / AutoStart
- Firmware: debounce counter now resets when a glitch settles back to the confirmed
  state (prevents a later real transition from inheriting stale counts → false trigger);
  USB config descriptor `bmAttributes` corrected 0xE0 → 0x80 (bus-powered, no remote
  wakeup — the device is bus-powered and doesn't use remote wakeup)

## v0.15.1 — 2026-11-07

- Fixed English UI falling back to Chinese status texts after a default-device change
  (`UpdateStatusTexts` now localizes)
- Stand mapping titles are now generated per language at display time ("支架 FF05" /
  "Stand FF05") instead of persisting Chinese text in settings
- Fixed a memory leak: `PROPVARIANT` strings from `IPropertyStore` are now cleared
  (`PropVariantClear`) and validated (`VT_LPWSTR`) — the friendly-name path ran on every
  enumeration, i.e. on every stand event
- Removed dormant code: `HotkeyService` (global hotkey feature, never active in the
  product) and the unused `HidDeviceInfo` model
- Added [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md) (ST SLA0044 / BSD-3 /
  Apache-2.0) and clarified that MIT covers original code only
- README: real hardware photos, wiring table, honest hardware status, download section

## v0.15.0 — 2026-11-07

- **Product name**: 自动音频控制 (zh) / Auto Audio Control (en)
- **UI language switch**: system / 中文 / English, ~60 strings extracted into language packs
  (`Themes/Lang.zh-CN.xaml`, `Themes/Lang.en-US.xaml`); tray tooltip and dialogs follow
- Tray context-menu separators now span the full menu width (custom dark style)
- "No stands found" hint no longer shows while a stand is connected

## v0.14.0 / v0.14.1 — 2026-11-04/05

- Rolling file logs (`Logs/`, daily, 7 days kept)
- Start-with-Windows (HKCU Run key, self-healing path) + single-instance guard
- Re-scan stands after sleep/resume (`WM_POWERBROADCAST`)
- Firmware: IWDG watchdog (register-level, ~2.4 s)
- v0.14.1: fixed autostart toggle that couldn't be turned off (WPF `MenuItem` toggles
  `IsChecked` before `Click`); unified themed dialogs (`AppMessageBox`)
- Root-caused and fixed the "duplicate frame storm": never re-issue `ReadFile` on an
  `OVERLAPPED` whose previous I/O is still pending (see ARCHITECTURE.md rule #1)

## v0.13.0 — 2026-11-04

- .NET 8 → 10; dropped WinForms / HidLibrary / System.Management — **zero NuGet dependencies**
- Tray rebuilt on raw `Shell_NotifyIcon` + WPF dark menus; HID enumeration via SetupAPI;
  hot-plug via `WM_DEVICECHANGE`
- Release model settled: single portable zip (~56 MB, self-contained, no installer)

## v0.12.0 — 2026-11-03

- Productization: protocol v2 (8-byte bidirectional frames, seq de-dup, ACK, firmware
  retransmit 300 ms × 4), multi-stand scheduler (last-lifted wins, fallback device),
  right-click device binding, single-page UI, per-stand settings persisted by device ID

## v0.11.0 and earlier — 2026-04

- Original demo: single-byte HID commands, device cards, tray, hotkeys, theme,
  first portable exe

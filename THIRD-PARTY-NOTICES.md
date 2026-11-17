# Third-Party Notices

This project combines original code with third-party libraries. The licenses below apply
to those third-party components; the top-level [LICENSE](LICENSE) (MIT) covers the
original code only.

## STMicroelectronics STM32 USB Device Library

- **Path**: `stm32/firmware/Middlewares/ST/STM32_USB_Device_Library/`
- **License**: SLA0044 (STMicroelectronics License Agreement 0044, also known as
  ST LA / "Ultimate Liberty" for the STM32 Cube ecosystem) — the full license text
  is embedded in the header of every source file in the library. Redistribution
  must keep those headers intact. The agreement can also be found on
  [st.com](https://www.st.com) by searching "SLA0044".

## STMicroelectronics STM32F1xx HAL Driver

- **Path**: `stm32/firmware/Drivers/STM32F1xx_HAL_Driver/`
- **License**: BSD-3-Clause — full text in
  `stm32/firmware/Drivers/STM32F1xx_HAL_Driver/LICENSE.txt`

## ARM CMSIS

- **Path**: `stm32/firmware/Drivers/CMSIS/`
- **License**: Apache-2.0 — full text in `stm32/firmware/Drivers/CMSIS/LICENSE.txt`

## Arduino-ESP32 core (Espressif) — `firmware-c3/`

- **Path**: linked into the compiled firmware images (`Firmware/*.bin`) bundled in
  the release zip; applied at build time via PlatformIO
- **License**: LGPL-2.1 — https://github.com/espressif/arduino-esp32
- The complete corresponding source (this project's firmware code, MIT, plus the
  pinned PlatformIO project file) lives in `firmware-c3/` of this repository, and
  the binaries are rebuilt from source with `pio run -d firmware-c3` — satisfying
  LGPL's relinking requirement for the distributed firmware.

## Microsoft .NET Runtime & WPF

- **Path**: bundled self-contained in `AudioCtrl.exe` / `*_cor3.dll`
- **License**: MIT — https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

## esptool (bundled standalone executable)

- **Path**: `Tools\esptool.exe` — **esptool v4.9.0** (Windows standalone build,
  PyInstaller-packaged with CPython and its dependencies)
- **License**: GPL-2.0-or-later — invoked as a separate process (mere
  aggregation, not linked into this app); full license text:
  https://www.gnu.org/licenses/old-licenses/gpl-2.0.txt
- **Source code**: https://github.com/espressif/esptool (tag v4.9.0)

## Acknowledgments

- The undocumented `IPolicyConfig` COM interface layout used for switching the default
  audio device is public ABI knowledge popularized by **EreTIk's policyconfig research**
  and the **[SoundSwitch](https://github.com/Belphemur/SoundSwitch)** project (GPL-3.0).
  This project declares its own interface stubs from that ABI layout; no third-party
  implementation code was copied.
- Project inspiration: SoundSwitch (hotkey-based switching) — AudioCtrl adds the
  physical "pick up / put down" trigger.

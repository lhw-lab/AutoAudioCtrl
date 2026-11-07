# Third-Party Notices

This project combines original code with third-party libraries. The licenses below apply
to those third-party components; the top-level [LICENSE](LICENSE) (MIT) covers the
original code only.

## STMicroelectronics STM32 USB Device Library

- **Path**: `firmware/Middlewares/ST/STM32_USB_Device_Library/`
- **License**: SLA0044 (STMicroelectronics License Agreement 0044, also known as
  ST LA / "Ultimate Liberty" for the STM32 Cube ecosystem) — the full license text
  is embedded in the header of every source file in the library. Redistribution
  must keep those headers intact. The agreement can also be found on
  [st.com](https://www.st.com) by searching "SLA0044".

## STMicroelectronics STM32F1xx HAL Driver

- **Path**: `firmware/Drivers/STM32F1xx_HAL_Driver/`
- **License**: BSD-3-Clause — full text in
  `firmware/Drivers/STM32F1xx_HAL_Driver/LICENSE.txt`

## ARM CMSIS

- **Path**: `firmware/Drivers/CMSIS/`
- **License**: Apache-2.0 — full text in `firmware/Drivers/CMSIS/LICENSE.txt`

## Acknowledgments

- The undocumented `IPolicyConfig` COM interface layout used for switching the default
  audio device is public ABI knowledge popularized by **EreTIk's policyconfig research**
  and the **[SoundSwitch](https://github.com/Belphemur/SoundSwitch)** project (GPL-3.0).
  This project declares its own interface stubs from that ABI layout; no third-party
  implementation code was copied.
- Project inspiration: SoundSwitch (hotkey-based switching) — AudioCtrl adds the
  physical "pick up / put down" trigger.

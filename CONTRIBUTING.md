# Contributing / 贡献指南

Thanks for your interest! / 感谢关注本项目！

## Welcome contributions / 欢迎贡献的方向

- **Enclosure design (STL/STEP)** — current reference models in [hardware/](hardware/);
  improvements welcome: a 3D-printable stand that houses the board
  cleanly. / **外壳设计**——规整容纳电路板的 3D 打印支架。
- **No-soldering enclosure** — the board's built-in BOOT button already works as a
  trigger (firmware dual-trigger source); a mechanical design where the headphone's
  weight presses the BOOT button directly would eliminate the external switch and
  soldering entirely. **Design constraint:** GPIO9 is a strapping pin; an enclosure
  that holds BOOT down at power-up would enter the ROM downloader. Keep BOOT
  released at power-up or use a non-strapping pin. / **免焊外壳**——板上 BOOT 键已被
  固件支持为触发源，设计一个机械结构让耳机重量直接压到 BOOT 键，即可完全省去外接
  开关和焊接。**设计约束**：GPIO9 是 strapping 脚，上电时压住 BOOT 会进下载模式；
  必须保证插电瞬间不压 BOOT，或改用非 strapping 引脚。
- **PCB design** — a minimal carrier board (MCU + micro switch + USB).
  / **PCB 设计**——极简载板（主控 + 微动开关 + USB）。
- **Translations** — add `AudioCtrl/Themes/Lang.<culture>.xaml` (copy
  `Lang.en-US.xaml`, translate values, keep keys), then wire it up:
  `LanguageService.Resolve` (culture name → dictionary), `MainWindow.LangKeys`
  (menu entry), and note that settings.json `Language` stores 0/1/2. Please
  open an issue first so the numbering can be reserved.
  / **翻译**——复制 `Lang.en-US.xaml` 翻译值，还需接线三处：`LanguageService.Resolve`
  的 culture 映射、`MainWindow.LangKeys` 菜单项、设置值编号；建议先开 issue 预留编号。
- **Bug fixes & tests** — `HidProtocol` and `SerialFrameCodec` are pure
  functions/parsers; `dotnet test tests/AudioCtrl.Tests/AudioCtrl.Tests.csproj`
  runs the existing unit suite. Add a failing test with a bug fix when possible.
  / **修 bug 和补测试**——`HidProtocol`/`SerialFrameCodec` 是纯函数/纯解析器；
  `dotnet test tests/AudioCtrl.Tests/AudioCtrl.Tests.csproj` 会跑现有单测；
  修 bug 时尽量先补一个失败用例。

## Ground rules / 基本约定

1. **Host app build must be clean**: `cd AudioCtrl && dotnet build` → 0 errors
   0 warnings (host app only — the C3 firmware build must be 0-warning too; the STM32 firmware has one known linker warning).
   / **上位机编译必须零警告**；C3 固件也必须零警告，STM32 固件有 1 个既有链接器警告。
2. Code comments are mostly **Chinese** (the author's working language) — English
   comments are equally welcome; there is no hard rule. UI strings must go into
   the language packs (`Loc.T`), never hardcode.
   / 代码注释以中文为主（作者工作语言），英文注释同样欢迎，不做强制；
   界面文案进语言包，不要硬编码。
3. Firmware custom code stays inside `/* USER CODE BEGIN/END */` blocks —
   the only exception is `USBD_SELF_POWERED` in `usbd_conf.h` (must stay 0 in
   the generated section; re-check after any CubeMX regeneration).
   / 固件自定义代码必须在 USER CODE 区内——唯一例外是 `usbd_conf.h` 的
   `USBD_SELF_POWERED`（在生成区保持 0，CubeMX 再生成后须复查）。
4. Don't break the rules in [ARCHITECTURE.md](AudioCtrl/docs/ARCHITECTURE.md)
   ("Hard-won rules"). / 不要违反架构文档里的铁律。
5. PRs: keep them small and described in Chinese or English.
   / PR 宜小而专注，中英文描述均可。

## Hardware notes / 硬件说明

See the wiring table in the README — reproducing the hardware takes an ESP32-C3 SuperMini
board, and a KW12 roller-lever micro switch. `/` 接线见 README，开关引脚插进板孔焊 2 个点即可复现。

# Contributing / 贡献指南

Thanks for your interest! / 感谢关注本项目！

## Welcome contributions / 欢迎贡献的方向

- **Enclosure design (STL/STEP)** — a nicer 3D-printable stand that houses the
  board cleanly. / **外壳设计**——更好看的 3D 打印支架，能规整地容纳电路板。
- **PCB design** — a minimal carrier board (MCU + micro switch + USB).
  / **PCB 设计**——极简载板（主控 + 微动开关 + USB）。
- **Translations** — add `AudioCtrl/Themes/Lang.<culture>.xaml` (copy
  `Lang.en-US.xaml`, translate values, keep keys), then wire it up:
  `LanguageService.Resolve` (culture name → dictionary), `MainWindow.LangKeys`
  (menu entry), and note that settings.json `Language` stores 0/1/2. Please
  open an issue first so the numbering can be reserved.
  / **翻译**——复制 `Lang.en-US.xaml` 翻译值，还需接线三处：`LanguageService.Resolve`
  的 culture 映射、`MainWindow.LangKeys` 菜单项、设置值编号；建议先开 issue 预留编号。
- **Bug fixes & tests** — `HidProtocol` is a pure static class, ideal for unit
  tests. / **修 bug 和补测试**——`HidProtocol` 是纯函数，最适合先加单测。

## Ground rules / 基本约定

1. **Host app build must be clean**: `cd AudioCtrl && dotnet build` → 0 errors
   0 warnings (host app only — the firmware build has one known linker warning).
   / **上位机编译必须零警告**（仅指上位机；固件有 1 个既有链接器警告）。
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

See the wiring table in the README — reproducing the hardware takes a Blue Pill
board, a micro switch and two wires. `/` 接线见 README，两根线即可复现。

# AGENTS.md — AI 助手入口

> 把本仓库交给任何 AI 编程助手前，先让它读这份文件。/ Give this file to any AI
> coding agent before it starts working on the repo.

## 项目是什么（30 秒）

耳机支架实体触发器：STM32 支架上的微动开关检测耳机拿起/放回 → USB HID
（8 字节帧，协议 v2，带 ACK/重传/去重）→ Windows 托盘程序切换默认音频设备。
拿起切耳机、全部放回切外放，多支架"后拿起者优先"。纯本地、零联网、零 NuGet 依赖。

## 当前状态（v0.16.1，2026-11）

| 部分 | 状态 |
|---|---|
| 上位机 `AudioCtrl/`（.NET 10 WPF） | ✅ 完成并**单支架**真机验证（含 115 次按键压测、静默自启、拔插重连）；多支架调度逻辑已实现，**≥2 台实测待做** |
| 固件 `firmware/`（STM32CubeIDE, F103C8T6） | ✅ 完成并烧录验证：消抖、ACK 重传、IWDG 看门狗、总线供电描述符 |
| 硬件 | 🚧 **能用的手工装置，不是成熟产品**：市售 Blue Pill 最小系统板 + 微动开关直焊（2 根线），装进手工掏空的 3D 打印支架。**定制 PCB 与正式外壳（STL/STEP）欢迎社区贡献**（见 CONTRIBUTING.md） |

真机已验证：握手/拿放切换/115 次按键压测/热插拔/睡眠唤醒对齐/静默启动。

## 目录与文档

| 路径 | 内容 |
|---|---|
| `AudioCtrl/` | Windows 上位机（C#） |
| `firmware/` | STM32 固件（自定义代码全部在 `USER CODE` 区内） |
| `AudioCtrl/docs/PROTOCOL.md` | HID 协议 v2 规范（帧格式/重传/ACK） |
| `AudioCtrl/docs/ARCHITECTURE.md` | 架构分层 + **铁律（勿违反）** + 调试技巧 |
| `AudioCtrl/docs/CHANGELOG.md` | 版本历史 |
| `CONTRIBUTING.md` | 贡献指南（欢迎方向：外壳/PCB/翻译/测试） |

## 构建与验证

```bash
# 上位机（需 .NET 10 SDK）——0 错误 0 警告为过关标准
cd AudioCtrl && dotnet build
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true  # 便携版 ~56MB

# 固件（需 STM32CubeIDE，headless）
stm32cubeidec.exe --launcher.suppressErrors -nosplash \
  -application org.eclipse.cdt.managedbuilder.core.headlessbuild \
  -data <workspace> -import <firmware 绝对路径> -cleanBuild firmware
# 烧录: STM32_Programmer_CLI -c port=SWD -w firmware/Debug/firmware.elf -v -rst
```

## 开发铁律（详见 ARCHITECTURE.md）

1. 代码注释以中文为主（作者维护效率），英文注释同样欢迎——不做强制；UI 文案一律进语言包（`Themes/Lang.*.xaml` + `Loc.T`），不硬编码
2. HID 读循环两条铁律：未决 I/O 的 OVERLAPPED 绝不复用；GetOverlappedResult 后必须 ResetEvent
3. PolicyConfig 用单一 CoClass CLSID + `as` 转换，接口需完整 12 方法 vtable
4. WPF 资源只向上查找（共享样式放 Window.Resources）；MenuItem 先翻转 IsChecked 再触发 Click
5. 支架映射按**设备 ID** 存储（只收 IsActive 端点）
6. 固件自定义代码只在 `USER CODE BEGIN/END` 之间（CubeMX 再生成不覆盖）。
   **唯一例外**：`USB_DEVICE/Target/usbd_conf.h` 的 `USBD_SELF_POWERED` 必须在
   CubeMX 生成区改 0（与 bmAttributes=0x80 总线供电一致，文件内有注释）；
   用 CubeMX 重新生成后必须检查它是否被改回 1

## 硬件复现（无需自制 PCB）

买任意 STM32F103C8T6 "Blue Pill" 最小系统板 + 一个鼠标键式微动开关，焊 2 根线：
PB0（输入，内部上拉）↔ 开关一端；PA6（输出低电平）↔ 开关另一端。
压住 = 耳机在架上 = 放回；弹起 = 拿起。实拍图见 README 硬件节。

固件烧录：**用户要求烧录固件时，按 [docs/FLASHING.md](AudioCtrl/docs/FLASHING.md)
逐步指导**（买 ST-Link → 装 CubeProgrammer → 接 4 根线 → 烧 hex → USB 验证；
用户接线后可让其拍照发来检查）。Release 的 hex 开箱即烧，无需编译固件。

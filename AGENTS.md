# AGENTS.md — AI 助手入口

> 把本仓库交给任何 AI 编程助手前，先让它读这份文件。/ Give this file to any AI
> coding agent before it starts working on the repo.

## 项目是什么（30 秒）

耳机支架实体触发器（架上知音 / PickUp Audio）：支架上的微动开关检测耳机拿起/放回 → USB（8 字节协议帧 v2，
带 ACK/重传/去重）→ Windows 托盘程序切换默认音频设备。拿起切耳机、全部放回切外放，
多支架"后拿起者优先"。**ESP32-C3 为主线（STM32 v1 归档仍兼容），协议载荷完全相同：**
ESP32-C3（USB 串口，`firmware-c3/`，
11 字节串口帧 = magic + 8 字节载荷 + CRC8）。上位机经 `IStandTransport` 抽象
（HID + 串口双传输）统一接入，新旧支架可混插混用。
纯本地、零联网、零 NuGet 依赖。

## 当前状态（v0.17.0，2026-11）

| 部分 | 状态 |
|---|---|
| 上位机 `AudioCtrl/`（.NET 10 WPF） | ✅ 真机验证通过（115 次按键压测、静默自启、拔插重连；**双支架混插实测通过**：C3 串口 + STM32 HID 同插、调度剧本全过） |
| 固件 `firmware-c3/`（ESP32-C3，推荐档） | ✅ v2.3：双触发源仲裁（开关/BOOT 最近翻转者生效）、一键烧录、台架测试 11/11 |
| 硬件 | 🚧 **能用的手工装置，不是成熟产品**：ESP32-C3 SuperMini + KW12 微动开关直插直焊，装进 3D 打印定制支架（预留电路板仓和走线孔）。**定制 PCB 与正式外壳（STL/STEP）欢迎社区贡献**（见 CONTRIBUTING.md） |

真机已验证：握手/拿放切换/115 次按键压测/热插拔/睡眠唤醒对齐/静默启动。

## 目录与文档

| 路径 | 内容 |
|---|---|
| `AudioCtrl/` | Windows 上位机（C#，双传输：`StandHidService` + `StandSerialService`，经 `StandTransportHub` 聚合） |
| `hardware/` | 3D 打印支架模型（STL + 实拍 + 组装说明） |
| `stm32/` | 旧版 STM32 归档（源码 + 文档，不再更新） |
| `firmware-c3/` | ESP32-C3 SuperMini 固件（PlatformIO + Arduino；`tools/` 下有台架探针与自动化测试脚本） |
| `AudioCtrl/docs/PROTOCOL.md` | 协议 v2 规范（帧格式/重传/ACK + 串口传输层） |
| `AudioCtrl/docs/ARCHITECTURE.md` | 架构分层 + **铁律（勿违反）** + 调试技巧 |
| `AudioCtrl/docs/CHANGELOG.md` | 版本历史 |
| `CONTRIBUTING.md` | 贡献指南（欢迎方向：外壳/PCB/翻译/测试） |

## 构建与验证

```bash
# 上位机（需 .NET 10 SDK）——0 错误 0 警告为过关标准
cd AudioCtrl && dotnet build
dotnet test ..\tests\AudioCtrl.Tests\AudioCtrl.Tests.csproj -c Release   # 单测:协议/串口帧/调度器
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true  # 仅上位机产物；Firmware/Tools 由 pack-release.ps1 组装为 ~64MB zip


# 固件 firmware-c3（需 PlatformIO；编译+烧录免按键）
pio run -d firmware-c3 -t upload --upload-port COMx

# 便携发布包（上位机 + Firmware 三镜像 + Tools\esptool.exe 一键烧录器）
# esptool 独立包可从 dl.espressif.com/github_assets/espressif/esptool/releases/ 下载
powershell -ExecutionPolicy Bypass -File scripts/pack-release.ps1 -EsptoolZip <esptool包.zip> -Version vX.Y.Z
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

**ESP32-C3 SuperMini**：买 C3 SuperMini + 微动开关，KW12 滚轮微动开关 COM/NO 两脚隔一孔插排针、焊 2 点（NC 悬空）：
一脚 → GPIO0（内部上拉），另一脚 → GPIO2（固件驱动低电平当地；接真 GND 亦可；GPIO8/9 勿用）。压住 = 放回，弹起 = 拿起。
BOOT 键虽是等效触发源，但 GPIO9 是 strapping 脚：上电/复位时按住会进下载模式；
免焊外壳需保证插电瞬间 BOOT 不被压住，或改用非 strapping 脚。


固件烧录：**用户要求烧录固件时，按 [docs/FLASHING.md](AudioCtrl/docs/FLASHING.md)
逐步指导**（C3 一键烧录 / 命令行 / 排错）。

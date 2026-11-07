# 烧录固件 / Flashing the Firmware

> 不懂烧录？把本文件发给任意 AI 助手（豆包 / DeepSeek / 你的编程智能体），说"带我完成这件事"，
> 它会一步步指导你；接完线拍张照片发给它检查。
> Not familiar with flashing? Send this file to any AI assistant and say
> "walk me through this" — it will guide you step by step.

## 流程总览 / Overview（懂的人看完就会）

```
买 ST-Link → 装 STM32CubeProgrammer → 接 4 根线 → 烧 firmware hex → 插 USB 验证
buy ST-Link → install STM32CubeProgrammer → wire 4 pins → flash hex → verify on USB
```

固件文件：仓库 Release 页面的 `firmware_vX.Y.Z.hex`（无需自己编译固件）。

## 硬事实 / Hard facts（AI 代劳的依据）

**① 要买什么**：ST-Link V2 兼容下载器（淘宝/拼多多搜"ST-Link V2"，约 10~20 元，含 4 根杜邦线更佳）。

**② 要装什么**：ST 官方 **STM32CubeProgrammer**（免费，官网 st.com 搜索该名称下载；
下载页可能要求填写邮箱，属正常）。Windows 10/11 一般免驱识别 ST-Link。

**③ 接线（共 4 根，别接错）**：

| ST-Link 引脚 | 板子 SWD 四针 |
|---|---|
| SWDIO | SWDIO |
| SWCLK | SWCLK |
| GND | GND |
| 3.3V | 3.3V |

- ⚠ **不要接到 5V**——可能烧板。
- 板上 SWD 四针的位置见 [README 实拍图](../../README.zh-CN.md)（焊好开关那张照片右上角的 4 针排座）。
- **BOOT0 跳线帽不用动**（SWD 烧录不需要碰它，保持默认即可）。

**④ 烧录命令**（装好 CubeProgrammer 后，管理员命令行；也可用其图形界面）：

```
STM32_Programmer_CLI -c port=SWD -w firmware_vX.Y.Z.hex -v -rst
```

**⑤ 成功判据**：拔掉 ST-Link，把板子 USB 口插到电脑——上位机界面 1 秒内出现"耳机支架"即成功。

**⑥ 常见报错**：

| 现象 | 原因 |
|---|---|
| `No target connected` | 线序错 / SWD 线没插紧 / 板子没供电 |
| 找不到 `STM32_Programmer_CLI` 命令 | 未装或未加入 PATH——用图形界面同样能烧 |
| ST-Link 灯不亮 / 无法识别 | 换 USB 口；装 ST 官方 ST-Link 驱动（STSW-LINK009） |

**⑦ 完全不想动手**：淘宝买板子时把 hex 文件发给卖家，请店家代烧（多数免费或一两元）。

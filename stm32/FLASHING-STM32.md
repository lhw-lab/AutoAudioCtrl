# STM32 烧录指南 / Flashing Guide (STM32)

> 本文档已从主项目 FLASHING.md 迁出，属于 [STM32 旧版硬件](README.md) 归档。

> 不懂烧录？把本文件发给任意 AI 助手，说"带我完成这件事"，
> 它会一步步指导你；接完线拍张照片发给它检查。

## 流程总览 / Overview

```
买 ST-Link → 装 STM32CubeProgrammer → 接 4 根线 → 烧 firmware hex → 插 USB 验证
```

## 硬事实 / Hard facts

**① 要买什么**：ST-Link V2 兼容下载器（淘宝约 10~20 元）。

**② 要装什么**：ST 官方 **STM32CubeProgrammer**（免费，st.com 下载）。

**③ 接线（共 4 根）**：

| ST-Link 引脚 | 板子 SWD 四针 |
|---|---|
| SWDIO | SWDIO |
| SWCLK | SWCLK |
| GND | GND |
| 3.3V | 3.3V |

⚠ **不要接 5V**。BOOT0 跳线帽不用动。

**④ 烧录命令**：

```
STM32_Programmer_CLI -c port=SWD -w firmware_v0.16.1.hex -v -rst
```

**⑤ 成功判据**：拔掉 ST-Link，板子 USB 插电脑 → 上位机 1 秒内出现"耳机支架"。

**⑥ 常见报错**：

| 现象 | 原因 |
|---|---|
| `No target connected` | 线序错 / SWD 没插紧 / 板子没供电 |
| 找不到 `STM32_Programmer_CLI` | 未装或未加 PATH——用图形界面 |
| ST-Link 不识别 | 换 USB 口；装 STSW-LINK009 驱动 |

**⑦ 完全不想动手**：淘宝买板时把 hex 发给卖家代烧。

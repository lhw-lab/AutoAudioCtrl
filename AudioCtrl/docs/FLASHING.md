# 烧录固件 / Flashing the Firmware

> 不懂烧录？把本文件发给任意 AI 助手，说"带我完成这件事"，
> 它会一步步指导你。

## ESP32-C3 一键烧录（唯一支持路线）

**不需要买烧录器，不需要装驱动，不需要按板上任何按键。**
C3 的 USB 是原生 Serial/JTAG，Win10/11 系统自带驱动，esptool 经 USB 复位
自动进入烧录模式——整个流程就是"插上 → 点一下"。

### 方式 A · 上位机界面内烧录（普通用户）

1. 装好 AudioCtrl 上位机（见 README"下载与使用"）
2. 把新板插到电脑 USB 口（**注意用数据线，不能用充电线**）
3. 主界面耳机支架区右上角点"**刷固件**"
4. 端口列表里选新板对应的 COM 口（插上新板后会自动出现在候选里）→ 点"开始烧录"
5. 约 2 秒完成，板子自动重启，稍候被识别为"耳机支架"——关闭窗口去绑定设备即可

### 方式 B · 命令行（开发者）

```bash
# 需 PlatformIO(VSCode 插件或 CLI);编译 + 烧录一步完成
pio run -d firmware-c3 -t upload --upload-port COMx
```

或直接用 esptool 写 Release 提供的三个镜像（偏移固定）：

```bash
esptool --chip esp32c3 --port COMx --baud 921600 --after hard_reset write_flash ^
  0x0 bootloader.bin 0x8000 partitions.bin 0x10000 firmware.bin
```

### 常见问题

| 现象 | 原因 / 处理 |
|---|---|
| 列表里没有端口 | USB 线是充电线（换数据线）；或板子不是原生 USB（带 CH340 的板，换 SuperMini） |
| 烧录失败 `Failed to connect` | 换 USB 口 / 换线重试；按住 BOOT 再点一次开始（手动进烧录模式兜底） |
| 烧录中报错端口被占用 | 关掉可能占着串口的程序（串口监视器/其他烧录工具）再试 |
| 烧完没被识别 | 等 2 秒；拔插一次 USB；看上位机日志 Logs/ 目录 |

---

> 旧版 STM32 硬件的烧录指南已迁至 [stm32/FLASHING-STM32.md](../../stm32/FLASHING-STM32.md)。

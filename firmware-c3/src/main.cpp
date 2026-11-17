/*
 * PickUp Audio(架上知音) 支架固件 · ESP32-C3 SuperMini(v2 硬件平台)
 *
 * 通信: USB Serial/JTAG(CDC),Win10/11 系统内置驱动免安装
 * 协议: 串口传输层帧 = magic(A5 5A) + 8 字节载荷 + CRC8,定长 11 字节
 *       载荷格式与 HID 协议 v2 的 8 字节帧完全一致(EVENT/INFO/ACK/QUERY、
 *       seq 去重、300ms×4 重传语义全部沿用),见 AudioCtrl/docs/PROTOCOL.md
 *       —— 上位机 HidProtocol 编解码可原样复用,仅传输层不同
 *
 * 接线(免焊,杜邦母头直插):
 *   微动开关一脚 → GPIO0(内部上拉)
 *   微动开关另一脚 → GPIO2(固件驱动为低电平,当"地"用;接真 GND 亦可)
 *   压住(导通,低电平) = 耳机在架上(放回);弹起(高电平) = 拿起
 *   线缆脱落 = 高电平 = 判为"拿起"(失效方向:切到耳机,不会外放漏音)
 *   注:GPIO2 为 strapping 脚,复位时内部默认上拉、对侧 GPIO0 复位时高阻,
 *       压着开关断电重启也不会误进下载模式
 *
 * 双触发源: 微动开关(GPIO0)与板上 BOOT 键(GPIO9)互为等效传感器,
 *           最近发生按压/松开翻转的键接管触发(不焊开关可直接用 BOOT);
 *           两键语义一致:按下=在架上(放回),松开=拿起
 * 远程 TEST 命令(台架脚本用): 单次翻转传感器状态——注意与 BOOT 键的
 *           状态式语义不同,tools/stand_test.py 按"每次 TEST 恰好一个事件"断言
 */

#include <Arduino.h>
#include <esp_mac.h>
#include <esp_task_wdt.h>

// ===================== 协议常量(载荷 = HID 协议 v2) =====================
static const uint8_t MAGIC0 = 0xA5;   // 传输层帧头
static const uint8_t MAGIC1 = 0x5A;

#define UP_MSG_EVENT   0x01  /* 上行:拿起/放回事件(需要 ACK,带重传) */
#define UP_MSG_INFO    0x04  /* 上行:应答 QUERY 的自报身份帧 / 连接 banner */
#define DN_MSG_ACK     0x01  /* 下行:事件确认 */
#define DN_MSG_QUERY   0x03  /* 下行:身份/状态查询 */
#define DN_MSG_TEST    0x05  /* 下行:台架测试(载荷[1]=0x01 单次翻转传感器状态;
                                  供自动化测试/CI 在环验证) */

#define EV_LIFT        0x01  /* 事件:耳机拿起 */
#define EV_PLACE       0x02  /* 事件:耳机放回 */

#define FW_VERSION     0x23  /* [2]=固件版本 ×16(0x23 = v2.3:双触发源仲裁,开关/BOOT 最近翻转者生效) */

// ===================== 引脚与行为参数 =====================
#define PIN_SENSOR     0     /* 微动开关(内部上拉,高=拿起) */
#define PIN_SENSOR_GND 2     /* 开关另一端:输出低电平当"地"用(接真 GND 亦可,不影响) */
#define PIN_BOOT       9     /* BOOT 键:状态式双触发源之一(按下=在架上,松开=拿起),与微动开关仲裁 */
#define DEBOUNCE_MS    30    /* 消抖:连续采样稳定时间(与 STM32 版一致) */
#define RETRY_MS       300   /* 未收到 ACK 的重发间隔 */
#define MAX_ATTEMPTS   4     /* 总尝试次数(首发+3 重试),超限放弃 */

#define ECHO_ON_ACK    1     /* 1=收到 ACK 立即回一帧 INFO(ACK 回执):
                                台架测主机→固件→主机 RTT 用;发布版可置 0 */

// ===================== 运行状态 =====================
static uint16_t dev_id = 0;         /* 本支架唯一 ID(eFuse MAC 的 FNV-1a 哈希) */
static uint8_t  tx_seq = 0;         /* 上行帧序号,1~255 循环 */

/* 待确认事件(同一时刻最多一个,新事件覆盖旧事件 —— 与 STM32 版一致) */
static bool     pend_active = false;
static uint8_t  pend_seq = 0;
static uint8_t  pend_event = 0;
static uint8_t  pend_attempts = 0;
static uint32_t pend_last = 0;

static bool     sensor_lifted = true;  /* 消抖后的传感器稳定状态(true=拿起) */
static uint8_t  deb_count = 0;         /* 消抖计数 */

/* ---- 双触发源仲裁:微动开关(GPIO0)与板上 BOOT 键(GPIO9)互为等效传感器,
   最近发生按压/松开翻转的键接管触发;另一键靠边站,直到它自己发生变化。
   两键电气语义一致:按下(低电平)=耳机在架上(放回),松开(高电平)=拿起。
   典型场景:不焊微动开关时直接拿 BOOT 当传感器;焊好后动一下开关即切回 ---- */
typedef enum { SRC_SWITCH = 0, SRC_BOOT = 1 } sensor_src_t;
static sensor_src_t active_src = SRC_SWITCH;  /* 上电默认微动开关为触发源 */
static bool     sw_stable = true;   /* 微动开关消抖后电平(true=松开/拿起) */
static uint8_t  sw_count = 0;       /* 微动开关消抖计数 */
static bool     btn_stable = true;  /* BOOT 键消抖后电平(true=松开/拿起) */
static uint8_t  btn_count = 0;      /* BOOT 键消抖计数 */

static bool     test_flip = false;     /* 远程 TEST 命令的传感器翻转位(单次翻转,
                                          保持旧语义:台架脚本按"每次 TEST 恰好一个
                                          事件"断言,勿改成脉冲式) */
static bool     cdc_was_up = false;    /* CDC 连接沿检测(连上即发 banner) */
static bool     host_seen = false;     /* 收到过主机任何下行帧(之后停发周期 banner) */
static uint32_t last_banner = 0;       /* 上次周期 banner 时刻 */

// ===================== CRC8(多项式 0x07,初值 0,MSB 先行) =====================
static uint8_t crc8(const uint8_t *d, size_t n)
{
    uint8_t c = 0;
    while (n--)
    {
        c ^= *d++;
        for (int i = 0; i < 8; i++)
            c = (c & 0x80) ? (uint8_t)((c << 1) ^ 0x07) : (uint8_t)(c << 1);
    }
    return c;
}

// ===================== 发帧 =====================
/* 发一帧: magic + 8 字节载荷 + CRC8(载荷原样,与 HID 帧同构)
   CDC 未连接(主机未开端口/瞬断)时直接丢弃:
   HWCDC::write 在未连接状态下可能长时间阻塞,会把主循环挂死喂不了看门狗;
   事件本就有 300ms×4 重传兜底,主机离线期间丢帧与 STM32 版语义一致 */
static void sendFrame(const uint8_t payload[8])
{
    if (!(bool)Serial) return;
    uint8_t f[11] = { MAGIC0, MAGIC1 };
    memcpy(f + 2, payload, 8);
    f[10] = crc8(payload, 8);
    Serial.write(f, sizeof(f));
}

/* 发送事件帧(首发与重发共用,seq 不变,上位机按 seq 去重) */
static void sendEventFrame(void)
{
    uint8_t p[8] = { UP_MSG_EVENT, pend_seq, pend_event,
                     (uint8_t)(dev_id & 0xFF), (uint8_t)(dev_id >> 8), 0, 0, 0 };
    sendFrame(p);
}

/* 发送身份帧:应答 QUERY 或 CDC 连接 banner(无需 ACK,丢了上位机会再查) */
static void sendInfoFrame(void)
{
    uint8_t p[8] = { UP_MSG_INFO, 0, FW_VERSION,
                     (uint8_t)(dev_id & 0xFF), (uint8_t)(dev_id >> 8),
                     (uint8_t)(sensor_lifted ? 1 : 0), 0, 0 };
    sendFrame(p);
}

/* 消抖确认的传感器翻转 → 生成事件(seq 自增,1~255 循环) */
static void fireEvent(uint8_t evt)
{
    if (++tx_seq == 0) tx_seq = 1;
    pend_active = true;
    pend_seq = tx_seq;
    pend_event = evt;
    pend_attempts = 1;
    pend_last = millis();
    sendEventFrame();
}

// ===================== 下行帧解析(字节流按 magic 重同步) =====================
enum ParseState { ST_M0, ST_M1, ST_PAYLOAD, ST_CRC };
static ParseState pst = ST_M0;
static uint8_t pbuf[8];
static uint8_t pidx = 0;

static void handlePayload(const uint8_t p[8])
{
    host_seen = true;   /* 收到主机任何帧:停发周期 banner,改由 QUERY 驱动 */
    if (p[0] == DN_MSG_ACK && p[1] == pend_seq)
    {
        pend_active = false;   /* 事件已确认,停止重传 */
#if ECHO_ON_ACK
        sendInfoFrame();       /* ACK 回执:测 RTT/确认主机下行已送达 */
#endif
    }
    else if (p[0] == DN_MSG_QUERY)
    {
        sendInfoFrame();
    }
    else if (p[0] == DN_MSG_TEST && p[1] == 0x01)
    {
        /* 台架测试(远程):翻转一次传感器状态 → 恰好一个事件(拿起/放回交替)。
           保持单次翻转语义——tools/stand_test.py 的 T3/T5 按"每次 TEST 恰好
           一个事件"断言,勿改成脉冲式 */
        test_flip = !test_flip;
    }
    /* 其他帧类型:忽略 */
}

static void feedParser(void)
{
    while (Serial.available() > 0)
    {
        uint8_t b = (uint8_t)Serial.read();
        switch (pst)
        {
        case ST_M0:
            if (b == MAGIC0) pst = ST_M1;
            break;
        case ST_M1:
            pst = (b == MAGIC1) ? ST_PAYLOAD : ST_M0;
            if (pst == ST_PAYLOAD) pidx = 0;
            break;
        case ST_PAYLOAD:
            pbuf[pidx++] = b;
            if (pidx >= 8) pst = ST_CRC;
            break;
        case ST_CRC:
            if (crc8(pbuf, 8) == b) handlePayload(pbuf);
            pst = ST_M0;   /* 无论成败都回到找帧头状态(容错日志噪音) */
            break;
        }
    }
}

// ===================== setup / loop =====================
void setup()
{
    /* Serial = USB CDC(见 platformio.ini 的 USB_MODE/CDC_ON_BOOT) */
    Serial.begin(115200);
    Serial.setDebugOutput(false);   /* 运行期禁止内核日志混入协议流 */

    pinMode(PIN_SENSOR, INPUT_PULLUP);
    /* 开关"地"端:先写低电平再切输出,避免初始化瞬间输出高(与 STM32 版 PA6 同套路) */
    digitalWrite(PIN_SENSOR_GND, LOW);
    pinMode(PIN_SENSOR_GND, OUTPUT);
    pinMode(PIN_BOOT, INPUT_PULLUP);

    /* 支架 ID:6 字节 eFuse MAC 的 FNV-1a 哈希折 16 位(算法与 STM32 版一致,
       全网芯片唯一且烧录/重启不变,多支架天然区分) */
    uint8_t mac[6];
    esp_read_mac(mac, ESP_MAC_WIFI_STA);
    uint32_t h = 0x811C9DC5UL;
    for (int i = 0; i < 6; i++)
    {
        h ^= mac[i];
        h *= 0x01000193UL;
    }
    dev_id = (uint16_t)(h ^ (h >> 16));

    /* 任务看门狗:对齐 STM32 版 IWDG(挂死 ~4s 内自复位,复位后上位机经
       QUERY 重新对齐状态)。系统若已初始化过 WDT,重复 init 返回错误可忽略 */
    esp_task_wdt_init(4, true);
    esp_task_wdt_add(NULL);

    sensor_lifted = true;   /* 上电默认按"拿起"处理,首次真实翻转经消抖修正 */
}

void loop()
{
    esp_task_wdt_reset();

    /* ---- 1. 身份广播:CDC 连接沿立即发 banner;收到主机任何下行帧之前每 2s
       周期重发(主机发现与复位重枚举后自恢复,不依赖 DTR 边沿检测) ---- */
    bool up = (bool)Serial;
    if (up && !cdc_was_up) sendInfoFrame();
    cdc_was_up = up;
    if (up && !host_seen && (uint32_t)(millis() - last_banner) >= 2000)
    {
        last_banner = millis();
        sendInfoFrame();
    }

    /* ---- 2. 双触发源:各自独立 30ms 消抖,翻转沿做源仲裁 ----
       最近发生按压/松开翻转的键接管为当前触发源(另一键闲置待命)。
       两键语义一致:松开(高电平)=拿起,按下(低电平)=在架上(放回) */
    bool sw_raw = (digitalRead(PIN_SENSOR) != LOW);
    if (sw_raw == sw_stable) sw_count = 0;
    else if (++sw_count >= DEBOUNCE_MS)
    {
        sw_stable = sw_raw;
        sw_count = 0;
        active_src = SRC_SWITCH;   /* 开关状态翻转 → 开关接管 */
    }

    bool btn_raw = (digitalRead(PIN_BOOT) != LOW);
    if (btn_raw == btn_stable) btn_count = 0;
    else if (++btn_count >= DEBOUNCE_MS)
    {
        btn_stable = btn_raw;
        btn_count = 0;
        active_src = SRC_BOOT;     /* BOOT 键状态翻转 → BOOT 接管 */
    }

    /* ---- 3. 有效传感器状态 = 当前源的电平 → 远程 TEST 翻转 → 30ms 消抖出事件 ---- */
    bool lifted = (active_src == SRC_BOOT ? btn_stable : sw_stable) ^ test_flip;
    if (lifted == sensor_lifted) deb_count = 0;
    else if (++deb_count >= DEBOUNCE_MS)
    {
        sensor_lifted = lifted;
        deb_count = 0;
        fireEvent(sensor_lifted ? EV_LIFT : EV_PLACE);
    }

    /* ---- 4. 未确认事件重传(300ms 间隔,首发+3 重试后放弃) ---- */
    if (pend_active && (uint32_t)(millis() - pend_last) >= RETRY_MS)
    {
        if (pend_attempts >= MAX_ATTEMPTS)
        {
            pend_active = false;   /* 视为主机离线,放弃 */
        }
        else
        {
            pend_attempts++;
            pend_last = millis();
            sendEventFrame();
        }
    }

    /* ---- 5. 收下行帧(ACK/QUERY) ---- */
    feedParser();

    delay(1);   /* ~1ms 主循环节拍(消抖计数的采样基准) */
}

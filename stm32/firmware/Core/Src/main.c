/* USER CODE BEGIN Header */
/**
  ******************************************************************************
  * @file           : main.c
  * @brief          : Main program body
  ******************************************************************************
  * @attention
  *
  * Copyright (c) 2026 STMicroelectronics.
  * All rights reserved.
  *
  * This software is licensed under terms that can be found in the LICENSE file
  * in the root directory of this software component.
  * If no LICENSE file comes with this software, it is provided AS-IS.
  *
  ******************************************************************************
  */
/* USER CODE END Header */
/* Includes ------------------------------------------------------------------*/
#include "main.h"
#include "usb_device.h"

/* Private includes ----------------------------------------------------------*/
/* USER CODE BEGIN Includes */
#include "usbd_hid.h"
/* USER CODE END Includes */

/* Private typedef -----------------------------------------------------------*/
/* USER CODE BEGIN PTD */

/* USER CODE END PTD */

/* Private define ------------------------------------------------------------*/
/* USER CODE BEGIN PD */
/* ---- 协议 v2 帧定义(固定 8 字节) ----
   上行(支架→电脑): [0]=类型 [1]=seq [2]=事件 [3..4]=支架ID(小端) [5..7]=预留
   下行(电脑→支架): [0]=类型 [1]=ack_seq [2]=结果 [3..7]=预留          */
#define UP_MSG_EVENT   0x01  /* 上行:拿起/放回事件(需要 ACK,带重传) */
#define UP_MSG_INFO    0x04  /* 上行:应答 QUERY 的自报身份帧(无需 ACK) */
#define DN_MSG_ACK     0x01  /* 下行:事件确认 */
#define DN_MSG_QUERY   0x03  /* 下行:身份/状态查询 */

#define EV_LIFT        0x01  /* 事件:耳机拿起 */
#define EV_PLACE       0x02  /* 事件:耳机放回 */

#define ACK_RESULT_OK  0x00  /* 上位机执行成功 */

#define RETRY_MS       300   /* 未收到 ACK 的重发间隔 */
#define MAX_ATTEMPTS   4     /* 总尝试次数(首发 + 3 次重试),超限放弃 */
/* USER CODE END PD */

/* Private macro -------------------------------------------------------------*/
/* USER CODE BEGIN PM */

/* USER CODE END PM */

/* Private variables ---------------------------------------------------------*/

/* USER CODE BEGIN PV */
extern USBD_HandleTypeDef hUsbDeviceFS;

static uint16_t dev_id = 0;     /* 本支架唯一 ID(96 位 UID 的哈希,上电算一次) */
static uint8_t  tx_seq = 0;     /* 上行帧序号,0~255 循环 */

/* 待确认事件(同一时刻最多一个,新事件覆盖旧事件) */
static uint8_t  pend_active = 0;    /* 1=有事件等待 ACK */
static uint8_t  pend_seq = 0;       /* 等待确认的序号 */
static uint8_t  pend_event = 0;     /* EV_LIFT / EV_PLACE */
static uint8_t  pend_attempts = 0;  /* 已尝试发送次数 */
static uint32_t pend_last = 0;      /* 上次发送时刻 */
/* USER CODE END PV */

/* Private function prototypes -----------------------------------------------*/
void SystemClock_Config(void);
static void MX_GPIO_Init(void);
/* USER CODE BEGIN PFP */

/* USER CODE END PFP */

/* Private user code ---------------------------------------------------------*/
/* USER CODE BEGIN 0 */
/* 发送事件帧(首发与重发共用,seq 不变,上位机按 seq 去重) */
static void SendEventFrame(void)
{
  uint8_t f[8] = { UP_MSG_EVENT, pend_seq, pend_event,
                   (uint8_t)(dev_id & 0xFFU), (uint8_t)(dev_id >> 8), 0, 0, 0 };
  USBD_HID_SendReport(&hUsbDeviceFS, f, sizeof(f));
}

/* 发送自报身份帧:应答上位机 QUERY(无需 ACK,丢了上位机会再查) */
static void SendInfoFrame(uint8_t sensor_lifted)
{
  uint8_t f[8] = { UP_MSG_INFO, 0, 0x20,   /* [2]=固件协议版本 v2.0 */
                   (uint8_t)(dev_id & 0xFFU), (uint8_t)(dev_id >> 8),
                   sensor_lifted, 0, 0 };   /* [5]=当前传感器状态 */
  USBD_HID_SendReport(&hUsbDeviceFS, f, sizeof(f));
}
/* USER CODE END 0 */

/**
  * @brief  The application entry point.
  * @retval int
  */
int main(void)
{

  /* USER CODE BEGIN 1 */

  /* USER CODE END 1 */

  /* MCU Configuration--------------------------------------------------------*/

  /* Reset of all peripherals, Initializes the Flash interface and the Systick. */
  HAL_Init();

  /* USER CODE BEGIN Init */

  /* USER CODE END Init */

  /* Configure the system clock */
  SystemClock_Config();

  /* USER CODE BEGIN SysInit */

  /* USER CODE END SysInit */

  /* Initialize all configured peripherals */
  MX_GPIO_Init();
  MX_USB_DEVICE_Init();
  /* USER CODE BEGIN 2 */
  /* 上电默认状态：按键视为抬起 → LED 灭。
     （GPIO 初始化把 PC13 默认写成了低电平=亮，这里纠正回来） */
  HAL_GPIO_WritePin(led_GPIO_Port, led_Pin, GPIO_PIN_SET);

  /* 计算 96 位 UID 的 FNV-1a 哈希作为本支架唯一 ID
     (STM32F1 UID 起始地址 0x1FFFF7E8,3 个 32 位字) */
  {
    const uint32_t *uid = (const uint32_t *)0x1FFFF7E8UL;
    uint32_t h = 0x811C9DC5UL;
    for (int i = 0; i < 3; i++)
    {
      h ^= uid[i];
      h *= 0x01000193UL;
    }
    dev_id = (uint16_t)(h ^ (h >> 16));
  }

  /* 独立看门狗 IWDG（寄存器直操，不依赖 HAL_IWDG 模块——CubeMX 未启用该模块，
     且此写法全部在 USER CODE 区，CubeMX 重新生成不受影响）。
     LSI 典型 40kHz（范围 30~60kHz），预分频 /32 → 约 1.25kHz，
     重装 3000 → 超时约 2.4s（范围 1.6~3.2s）。
     主循环每毫秒喂一次狗，正常跑远快于超时；死机/跑飞 2 秒内自动复位，
     复位后 USB 重新枚举，上位机靠 QUERY 重新对齐状态。 */
  IWDG->KR  = 0x5555U;         /* 解锁 PR/RLR 写入 */
  IWDG->PR  = 3U;              /* 预分频 /32 */
  IWDG->RLR = 3000U - 1U;      /* 重装值 */
  IWDG->KR  = 0xAAAAU;         /* 先喂一次，让 RLR 从影子寄存器生效 */
  IWDG->KR  = 0xCCCCU;         /* 启动看门狗 */
  /* USER CODE END 2 */

  /* Infinite loop */
  /* USER CODE BEGIN WHILE */
  while (1)
  {
    /* USER CODE BEGIN 3 */
    /* ===== 1. 传感器采样 + 30ms 状态型消抖 =====
       语义(真实支架方向):耳机放在架上压住传感器 = 低电平 = "放回";
       耳机拿起传感器弹起 = 高电平(内部上拉) = "拿起" */
    uint8_t level = (HAL_GPIO_ReadPin(KEY_GPIO_Port, KEY_Pin) == GPIO_PIN_RESET)
                        ? 0    /* 低电平 = 传感器被压 = 耳机在架上 */
                        : 1;   /* 高电平 = 传感器弹起 = 耳机被拿起 */

    static uint8_t confirmed = 1;   /* 已确认状态,上电默认在架上(1) */
    static uint8_t last_sample = 1; /* 上一次采样值 */
    static uint8_t stable_count = 0;/* 新电平连续出现次数 */

    if (level != confirmed)
    {
      if (level == last_sample)
        stable_count++;
      else
      {
        last_sample = level;
        stable_count = 1;
      }

      if (stable_count >= 30)   /* 连续 30ms → 确认状态变化 → 生成事件 */
      {
        confirmed = level;
        stable_count = 0;
        tx_seq++;                                   /* 新序号 */
        pend_active = 1;
        pend_seq = tx_seq;
        pend_event = (confirmed == 0) ? EV_PLACE : EV_LIFT;   /* 压下=放回,弹起=拿起 */
        pend_attempts = 0;                          /* 由下方状态机统一发送 */
      }
    }
    else
    {
      /* 瞬时干扰消失(回到已确认状态):清掉残留的计数与采样值。
         否则短暂毛刺留下的 stable_count/last_sample 会让下一次同方向
         的真实扰动从旧计数"续命",提前满足 30ms 条件造成误触发 */
      stable_count = 0;
      last_sample = confirmed;
    }

    /* ===== 2. 事件发送/重传(300ms 间隔,最多 4 次) ===== */
    if (pend_active != 0)
    {
      uint32_t now = HAL_GetTick();
      if (pend_attempts == 0 || (now - pend_last) >= RETRY_MS)
      {
        if (pend_attempts >= MAX_ATTEMPTS)
        {
          /* 重试耗尽仍无确认 → 放弃(上位机不在线等情形) */
          pend_active = 0;
        }
        else
        {
          SendEventFrame();
          pend_attempts++;
          pend_last = now;
        }
      }
    }

    /* ===== 3. 处理下行帧(ACK / QUERY) ===== */
    {
      uint8_t rx[8];
      if (USBD_HID_GetReport(&hUsbDeviceFS, rx) != 0)
      {
        if (rx[0] == DN_MSG_ACK && pend_active != 0 && rx[1] == pend_seq)
        {
          pend_active = 0;   /* 确认到达(无论成功失败),停止重传。LED 已不参与指示 */
        }
        else if (rx[0] == DN_MSG_QUERY)
        {
          /* 上位机刚连上/重新枚举,自报身份和当前传感器状态 */
          SendInfoFrame((confirmed != 0) ? 1U : 0U);   /* 弹起=拿起=1 */
        }
      }
    }

    HAL_Delay(1);   /* 1ms 轮询间隔(纯轮询,不睡眠) */

    /* 喂狗：每轮循环(约 1ms)一次，远快于 2.4s 超时 */
    IWDG->KR = 0xAAAAU;
  }
  /* USER CODE END 3 */
}

/**
  * @brief System Clock Configuration
  * @retval None
  */
void SystemClock_Config(void)
{
  RCC_OscInitTypeDef RCC_OscInitStruct = {0};
  RCC_ClkInitTypeDef RCC_ClkInitStruct = {0};
  RCC_PeriphCLKInitTypeDef PeriphClkInit = {0};

  /** Initializes the RCC Oscillators according to the specified parameters
  * in the RCC_OscInitTypeDef structure.
  */
  RCC_OscInitStruct.OscillatorType = RCC_OSCILLATORTYPE_HSE;
  RCC_OscInitStruct.HSEState = RCC_HSE_ON;
  RCC_OscInitStruct.HSEPredivValue = RCC_HSE_PREDIV_DIV1;
  RCC_OscInitStruct.HSIState = RCC_HSI_ON;
  RCC_OscInitStruct.PLL.PLLState = RCC_PLL_ON;
  RCC_OscInitStruct.PLL.PLLSource = RCC_PLLSOURCE_HSE;
  RCC_OscInitStruct.PLL.PLLMUL = RCC_PLL_MUL9;
  if (HAL_RCC_OscConfig(&RCC_OscInitStruct) != HAL_OK)
  {
    Error_Handler();
  }

  /** Initializes the CPU, AHB and APB buses clocks
  */
  RCC_ClkInitStruct.ClockType = RCC_CLOCKTYPE_HCLK|RCC_CLOCKTYPE_SYSCLK
                              |RCC_CLOCKTYPE_PCLK1|RCC_CLOCKTYPE_PCLK2;
  RCC_ClkInitStruct.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
  RCC_ClkInitStruct.AHBCLKDivider = RCC_SYSCLK_DIV1;
  RCC_ClkInitStruct.APB1CLKDivider = RCC_HCLK_DIV2;
  RCC_ClkInitStruct.APB2CLKDivider = RCC_HCLK_DIV1;

  if (HAL_RCC_ClockConfig(&RCC_ClkInitStruct, FLASH_LATENCY_2) != HAL_OK)
  {
    Error_Handler();
  }
  PeriphClkInit.PeriphClockSelection = RCC_PERIPHCLK_USB;
  PeriphClkInit.UsbClockSelection = RCC_USBCLKSOURCE_PLL_DIV1_5;
  if (HAL_RCCEx_PeriphCLKConfig(&PeriphClkInit) != HAL_OK)
  {
    Error_Handler();
  }
}

/**
  * @brief GPIO Initialization Function
  * @param None
  * @retval None
  */
static void MX_GPIO_Init(void)
{
  GPIO_InitTypeDef GPIO_InitStruct = {0};
  /* USER CODE BEGIN MX_GPIO_Init_1 */

  /* USER CODE END MX_GPIO_Init_1 */

  /* GPIO Ports Clock Enable */
  __HAL_RCC_GPIOC_CLK_ENABLE();
  __HAL_RCC_GPIOD_CLK_ENABLE();
  __HAL_RCC_GPIOA_CLK_ENABLE();

  /*Configure GPIO pin Output Level */
  HAL_GPIO_WritePin(led_GPIO_Port, led_Pin, GPIO_PIN_RESET);

  /*Configure GPIO pin : led_Pin */
  GPIO_InitStruct.Pin = led_Pin;
  GPIO_InitStruct.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct.Pull = GPIO_NOPULL;
  GPIO_InitStruct.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(led_GPIO_Port, &GPIO_InitStruct);

  /* USER CODE BEGIN MX_GPIO_Init_2 */
  /* 初始化按键 PB0：输入模式 + 内部上拉（按下经按键接到 PA6(低电平) = 低电平） */
  __HAL_RCC_GPIOB_CLK_ENABLE();
  GPIO_InitTypeDef GPIO_InitStruct_Key = {0};
  GPIO_InitStruct_Key.Pin = KEY_Pin;
  GPIO_InitStruct_Key.Mode = GPIO_MODE_INPUT;
  GPIO_InitStruct_Key.Pull = GPIO_PULLUP;
  HAL_GPIO_Init(KEY_GPIO_Port, &GPIO_InitStruct_Key);

  /* 初始化 PA6：推挽输出低电平，作为按键的"模拟 GND"。
     先写低电平再初始化引脚，避免初始化瞬间输出高电平 */
  HAL_GPIO_WritePin(KEY_GND_GPIO_Port, KEY_GND_Pin, GPIO_PIN_RESET);
  GPIO_InitTypeDef GPIO_InitStruct_KeyGnd = {0};
  GPIO_InitStruct_KeyGnd.Pin = KEY_GND_Pin;
  GPIO_InitStruct_KeyGnd.Mode = GPIO_MODE_OUTPUT_PP;
  GPIO_InitStruct_KeyGnd.Pull = GPIO_NOPULL;
  GPIO_InitStruct_KeyGnd.Speed = GPIO_SPEED_FREQ_LOW;
  HAL_GPIO_Init(KEY_GND_GPIO_Port, &GPIO_InitStruct_KeyGnd);
  /* USER CODE END MX_GPIO_Init_2 */
}

/* USER CODE BEGIN 4 */

/* USER CODE END 4 */

/**
  * @brief  This function is executed in case of error occurrence.
  * @retval None
  */
void Error_Handler(void)
{
  /* USER CODE BEGIN Error_Handler_Debug */
  /* User can add his own implementation to report the HAL error return state */
  __disable_irq();
  while (1)
  {
  }
  /* USER CODE END Error_Handler_Debug */
}
#ifdef USE_FULL_ASSERT
/**
  * @brief  Reports the name of the source file and the source line number
  *         where the assert_param error has occurred.
  * @param  file: pointer to the source file name
  * @param  line: assert_param error line source number
  * @retval None
  */
void assert_failed(uint8_t *file, uint32_t line)
{
  /* USER CODE BEGIN 6 */
  /* User can add his own implementation to report the file name and line number,
     ex: printf("Wrong parameters value: file %s on line %d\r\n", file, line) */
  /* USER CODE END 6 */
}
#endif /* USE_FULL_ASSERT */

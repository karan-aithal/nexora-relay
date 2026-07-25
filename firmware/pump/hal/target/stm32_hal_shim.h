/* Minimal prototypes representative of the STM32Cube HAL API surface the target adapter
 * uses. On real hardware these come from the vendor's stm32xxxx_hal.h; here they let the
 * target HAL *compile* under an arm-none-eabi cross-compiler in CI without pulling in the
 * whole Cube SDK. The target build is archived (compile-only), never linked or run, so the
 * externs stay unresolved deliberately (firmware/pump/CMakeLists.txt).
 *
 * SPEC-UNVERIFIED: exact signatures/handle types vary by STM32 family and Cube version.
 * These are shaped like the common F4 HAL but are NOT pinned to one; the real headers
 * replace this file on device. See docs/walkthroughs/04-*.md. */
#ifndef OFP_STM32_HAL_SHIM_H
#define OFP_STM32_HAL_SHIM_H

#include <stdint.h>

typedef enum { HAL_OK = 0, HAL_ERROR = 1, HAL_BUSY = 2, HAL_TIMEOUT = 3 } HAL_StatusTypeDef;

typedef struct { uint32_t reserved; } UART_HandleTypeDef;
typedef struct { uint32_t reserved; } GPIO_TypeDef;
typedef struct { uint32_t reserved; } IWDG_HandleTypeDef;

extern UART_HandleTypeDef huart_pump; /* controller UART, provided by CubeMX init */
extern GPIO_TypeDef *const OFP_VALVE_PORT;
extern GPIO_TypeDef *const OFP_LAMP_PORT;
extern IWDG_HandleTypeDef hiwdg;

#define OFP_VALVE_PIN ((uint16_t)0x0001)
#define OFP_LAMP_PIN ((uint16_t)0x0002)
#define GPIO_PIN_RESET 0
#define GPIO_PIN_SET 1

HAL_StatusTypeDef HAL_UART_Transmit(UART_HandleTypeDef *huart, const uint8_t *data,
                                    uint16_t size, uint32_t timeout);
HAL_StatusTypeDef HAL_UART_Receive(UART_HandleTypeDef *huart, uint8_t *data, uint16_t size,
                                   uint32_t timeout);
void HAL_GPIO_WritePin(GPIO_TypeDef *port, uint16_t pin, int state);
uint32_t HAL_GetTick(void);
HAL_StatusTypeDef HAL_IWDG_Refresh(IWDG_HandleTypeDef *hiwdg);
void ITM_SendChar(uint32_t ch); /* SWO trace, the target log sink (never printf) */

#endif /* OFP_STM32_HAL_SHIM_H */

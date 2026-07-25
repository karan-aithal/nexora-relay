/* Target HAL: the SAME portable core (src/) binds to the STM32 HAL through this adapter.
 * Compiled by CI under arm-none-eabi-gcc to prove the core is genuinely portable; it is
 * archived (compile-only), never linked or executed here. No printf — logging goes out the
 * SWO/ITM trace port (CLAUDE.md §6 C rules). No dynamic allocation. */
#include "../hal.h"

#include "stm32_hal_shim.h"

/* Registered timer callback (would be invoked from the flow-meter TIMx capture ISR). */
static void (*g_timer_cb)(void *ctx);
static void *g_timer_ctx;

void hal_uart_write(const uint8_t *data, size_t len)
{
    /* Blocking transmit is acceptable at the byte level on the target UART; the core never
     * assumes non-blocking on write. Timeout is generous but bounded. */
    HAL_UART_Transmit(&huart_pump, data, (uint16_t)len, 100u);
}

int hal_uart_read(uint8_t *buf, size_t cap)
{
    /* Non-blocking single-shot read: zero timeout returns HAL_TIMEOUT when the RX buffer is
     * empty. A real build would use interrupt/DMA RX into a ring; this keeps the core's
     * poll contract (0 = nothing available) intact. */
    HAL_StatusTypeDef st = HAL_UART_Receive(&huart_pump, buf, (uint16_t)cap, 0u);
    if (st == HAL_OK)
    {
        return (int)cap;
    }
    if (st == HAL_TIMEOUT)
    {
        return 0;
    }
    return -1;
}

void hal_gpio_set(int line, int level)
{
    int state = level ? GPIO_PIN_SET : GPIO_PIN_RESET;
    if (line == OFP_GPIO_VALVE)
    {
        HAL_GPIO_WritePin(OFP_VALVE_PORT, OFP_VALVE_PIN, state);
    }
    else if (line == OFP_GPIO_FAULT_LAMP)
    {
        HAL_GPIO_WritePin(OFP_LAMP_PORT, OFP_LAMP_PIN, state);
    }
}

uint32_t hal_millis(void)
{
    return HAL_GetTick();
}

void hal_timer_register(uint32_t period_ms, void (*callback)(void *ctx), void *ctx)
{
    (void)period_ms; /* real init configures TIMx at this period; see comment below */
    g_timer_cb = callback;
    g_timer_ctx = ctx;
    /* Real init would configure TIMx for period_ms and enable its ISR, which then calls
     * g_timer_cb(g_timer_ctx). Kept minimal for the compile-only target build. */
}

/* Would be called from the TIMx ISR on device. Referenced here so the stored state is used
 * and the watchdog is refreshed on the firmware's heartbeat. */
void ofp_target_timer_isr(void)
{
    if (g_timer_cb != 0)
    {
        g_timer_cb(g_timer_ctx);
    }
    HAL_IWDG_Refresh(&hiwdg);
}

void hal_log(const char *message)
{
    for (const char *c = message; *c != '\0'; c++)
    {
        ITM_SendChar((uint32_t)(unsigned char)*c);
    }
    ITM_SendChar((uint32_t)'\n');
}

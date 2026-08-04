#include <stdint.h>
#ifndef LED_H
#define LED_H

#define BLINK_GPIO 48
#define COLOR_CYCLE_INTERVAL_MS 300

/**
 * @brief Initialize the onboard RGB LED and start the color cycling task
 */
void led_init_onboard_rgb(void);

/**
 * @brief Set the color for the throb LED effect
 * @param red Red component (0-255)
 * @param green Green component (0-255)
 * @param blue Blue component (0-255)
 */
void led_set_throb_color(uint32_t red, uint32_t green, uint32_t blue);

#endif // LED_H

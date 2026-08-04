#include <freertos/FreeRTOS.h>
#include <freertos/task.h>
#include "led_strip.h"
#include "led.h"

/// LED strip common configuration
static led_strip_config_t strip_config = {
	.strip_gpio_num = BLINK_GPIO,  // The GPIO that connected to the LED strip's data line
	.max_leds = 1,                 // The number of LEDs in the strip,
	.led_model = LED_MODEL_WS2812, // LED strip model, it determines the bit timing
	.color_component_format = LED_STRIP_COLOR_COMPONENT_FMT_GRB, // The color component format is G-R-B
	.flags = {
		.invert_out = false, // don't invert the output signal
	}
};

/// RMT backend specific configuration
static led_strip_rmt_config_t rmt_config = {
	.clk_src = RMT_CLK_SRC_DEFAULT,    // different clock source can lead to different power consumption
	.resolution_hz = 10 * 1000 * 1000, // RMT counter clock frequency: 10MHz
	.mem_block_symbols = 64,           // the memory size of each RMT channel, in words (4 bytes)
	.flags = {
		.with_dma = true, // DMA feature is available on chips like ESP32-S3/P4
	}
};

/// LED strip object
static led_strip_handle_t led_strip = NULL;

/// Global variable to store the throb LED color (RGB format)
static uint32_t throb_color[3] = {128, 0, 0}; // Default to red

static void led_color_cycle_task(void *arg)
{
	// Define colors as RGB components (for GRB format LED)
	uint32_t colors[][3] = {
		{128, 0, 0}, // Red
		{0, 128, 0}, // Green
		{0, 0, 128}  // Blue
	};
	int color_index = 0;

	while (1) {
		ESP_ERROR_CHECK(led_strip_set_pixel(led_strip, 0, 
			colors[color_index][0], 
			colors[color_index][1], 
			colors[color_index][2]));
		ESP_ERROR_CHECK(led_strip_refresh(led_strip));
		
		color_index = (color_index + 1) % 3;
		vTaskDelay(pdMS_TO_TICKS(COLOR_CYCLE_INTERVAL_MS));
	}
}

static void throb_led(void *arg)
{
	const int brightness_steps = 64;  // number of steps to ramp up/down
	const int step_delay_ms = 20;     // delay between each step

	while (true) {
		// ramp up
		for (int brightness = 0; brightness <= brightness_steps; brightness++) {
			uint8_t scaled_r = (throb_color[0] * brightness) / brightness_steps;
			uint8_t scaled_g = (throb_color[1] * brightness) / brightness_steps;
			uint8_t scaled_b = (throb_color[2] * brightness) / brightness_steps;
			
			ESP_ERROR_CHECK(led_strip_set_pixel(led_strip, 0, scaled_r, scaled_g, scaled_b));
			ESP_ERROR_CHECK(led_strip_refresh(led_strip));
			vTaskDelay(pdMS_TO_TICKS(step_delay_ms));
		}

		// ramp down
		for (int brightness = brightness_steps; brightness >= 0; brightness--) {
			uint8_t scaled_r = (throb_color[0] * brightness) / brightness_steps;
			uint8_t scaled_g = (throb_color[1] * brightness) / brightness_steps;
			uint8_t scaled_b = (throb_color[2] * brightness) / brightness_steps;
			
			ESP_ERROR_CHECK(led_strip_set_pixel(led_strip, 0, scaled_r, scaled_g, scaled_b));
			ESP_ERROR_CHECK(led_strip_refresh(led_strip));
			vTaskDelay(pdMS_TO_TICKS(step_delay_ms));
		}
	}
}

void led_set_throb_color(uint32_t red, uint32_t green, uint32_t blue)
{
	throb_color[0] = red;
	throb_color[1] = green;
	throb_color[2] = blue;
}

void led_init_onboard_rgb(void)
{
	ESP_ERROR_CHECK(led_strip_new_rmt_device(&strip_config, &rmt_config, &led_strip));
	
	// Start the color cycling task
	//xTaskCreate(led_color_cycle_task, "led_color_cycle", 2048, NULL, 5, NULL);
	xTaskCreate(throb_led, "throb_led", 4096, NULL, 5, NULL);
}
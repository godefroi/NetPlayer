#include <string.h>
#include "esp_system.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "esp_heap_caps.h"
#include "freertos/FreeRTOS.h"
#include "lwip/err.h"
#include "lwip/sys.h"
#include "lwip/sockets.h"
#include "driver/i2s_std.h"
#include "led.h"

#define TAG "NetAudio"

// TCP server configuration
#define KEEPALIVE_IDLE      5
#define KEEPALIVE_INTERVAL  5
#define KEEPALIVE_COUNT     3

// I2S configuration
#define I2S_NUM             I2S_NUM_0
#define I2S_SAMPLE_RATE     48000
#define I2S_BCLK_PIN        4
#define I2S_WS_PIN          5
#define I2S_DOUT_PIN        6
#define I2S_BITS_PER_SAMPLE 32
#define I2S_CHANNELS        2  // Stereo

// Buffer Configuration
#define AUDIO_BUFFER_SIZE   4096

static i2s_chan_handle_t tx_handle = NULL;
static uint16_t tcp_port;

void audio_task(void *pvParameters)
{
	int sock = (int)pvParameters;
	uint8_t *buffer = heap_caps_malloc(AUDIO_BUFFER_SIZE, MALLOC_CAP_DMA);

	if (buffer == NULL) {
		ESP_LOGE(TAG, "Failed to allocate buffer");
		close(sock);
		vTaskDelete(NULL);
		return;
	}

	// throb green because we're connected
	led_set_throb_color(0, 128, 0);

	// enable the i2s channel
	ESP_ERROR_CHECK(i2s_channel_enable(tx_handle));

	uint64_t start_time = esp_timer_get_time();
	uint64_t total_bytes = 0;
	size_t bytes_written = 0;

	ESP_LOGI(TAG, "Audio task started");

	while (true) {
		// receive some data from the remote system
		int len = recv(sock, buffer, AUDIO_BUFFER_SIZE, 0);

		// handle the case where somethign went wrong
		if (len < 0) {
			ESP_LOGE(TAG, "Error receiving data: errno %d", errno);
			break;
		} else if (len == 0) {
			ESP_LOGI(TAG, "Connection closed");
			break;
		}

		total_bytes += len;
		
		// write the data over i2s
		esp_err_t ret = i2s_channel_write(tx_handle, buffer, len, &bytes_written, portMAX_DELAY);

		if (ret != ESP_OK) {
			ESP_LOGE(TAG, "I2S write failed: %s", esp_err_to_name(ret));
			break;
		}

		if (bytes_written < len) {
			ESP_LOGW(TAG, "Not all bytes written: %d/%d", bytes_written, len);
		}
	}

	// disable the i2s channel
	ESP_ERROR_CHECK(i2s_channel_disable(tx_handle));
	// TODO: a better way to handle this would be to keep the channel active at all times, and write zeroes when no data is incoming
	// TODO: also, the ESP-ADF has a whole audio pipeline abstraction that includes stuff like opus decoders...

	// print out some interesting stats
	uint64_t end_time = esp_timer_get_time();
	uint64_t elapsed_us = end_time - start_time;
	double elapsed_seconds = elapsed_us / 1000000.0;
	double total_megabytes = total_bytes / (1024.0 * 1024.0);
	double mbits_per_sec = (total_bytes * 8.0) / (elapsed_us > 0 ? elapsed_us : 1) * 1000000.0 / 1000000.0;

	ESP_LOGI(TAG, "Audio task ending");
	ESP_LOGI(TAG, "Total bytes received: %llu (%.2f MB)", total_bytes, total_megabytes);
	ESP_LOGI(TAG, "Transfer speed: %.2f Mbits/sec (over %.2f seconds)", mbits_per_sec, elapsed_seconds);

	// go back to throbbing red
	led_set_throb_color(128, 0, 0);

	// clean up
	free(buffer);
	close(sock);
	vTaskDelete(NULL);
}

// https://docs.espressif.com/projects/esp-idf/en/stable/esp32/api-reference/system/freertos_additions.html
// void audio_ringbuffer_task(void)
// {
// 	while (true) {
// 		xRingbufferReceiveUpTo()
// 	}
// }

void tcp_server_task(void *pvParameters)
{
	char addr_str[128];
	int addr_family = AF_INET;
	int ip_protocol = 0;
	int keepAlive = 1;
	int keepIdle = KEEPALIVE_IDLE;
	int keepInterval = KEEPALIVE_INTERVAL;
	int keepCount = KEEPALIVE_COUNT;
	struct sockaddr_storage dest_addr;

	if (addr_family == AF_INET) {
		struct sockaddr_in *dest_addr_ip4 = (struct sockaddr_in *)&dest_addr;
		dest_addr_ip4->sin_addr.s_addr = htonl(INADDR_ANY);
		dest_addr_ip4->sin_family = AF_INET;
		dest_addr_ip4->sin_port = htons(tcp_port);
		ip_protocol = IPPROTO_IP;
	}

	int listen_sock = socket(addr_family, SOCK_STREAM, ip_protocol);

	if (listen_sock < 0) {
		ESP_LOGE(TAG, "Unable to create socket: errno %d", errno);
		vTaskDelete(NULL);
		return;
	}

	int opt = 1;
	setsockopt(listen_sock, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

	ESP_LOGI(TAG, "Socket created");

	int err = bind(listen_sock, (struct sockaddr *)&dest_addr, sizeof(dest_addr));

	if (err != 0) {
		ESP_LOGE(TAG, "Socket unable to bind: errno %d", errno);
		ESP_LOGE(TAG, "IPPROTO: %d", addr_family);
		close(listen_sock);
		vTaskDelete(NULL);
	}

	ESP_LOGI(TAG, "Socket bound, port %d", tcp_port);

	err = listen(listen_sock, 1);

	if (err != 0) {
		ESP_LOGE(TAG, "Error occurred during listen: errno %d", errno);
		close(listen_sock);
		vTaskDelete(NULL);
	}

	while (1) {
		ESP_LOGI(TAG, "Socket listening");

		struct sockaddr_storage source_addr;
		socklen_t addr_len = sizeof(source_addr);
		int sock = accept(listen_sock, (struct sockaddr *)&source_addr, &addr_len);
		if (sock < 0) {
			ESP_LOGE(TAG, "Unable to accept connection: errno %d", errno);
			break;
		}

		// Set tcp keepalive option
		setsockopt(sock, SOL_SOCKET, SO_KEEPALIVE, &keepAlive, sizeof(int));
		setsockopt(sock, IPPROTO_TCP, TCP_KEEPIDLE, &keepIdle, sizeof(int));
		setsockopt(sock, IPPROTO_TCP, TCP_KEEPINTVL, &keepInterval, sizeof(int));
		setsockopt(sock, IPPROTO_TCP, TCP_KEEPCNT, &keepCount, sizeof(int));

		// Convert ip address to string
		if (source_addr.ss_family == PF_INET) {
			inet_ntoa_r(((struct sockaddr_in *)&source_addr)->sin_addr, addr_str, sizeof(addr_str) - 1);
		}

		ESP_LOGI(TAG, "Socket accepted ip address: %s", addr_str);

		// Create audio task to handle this connection
		xTaskCreate(audio_task, "audio_task", 4096, (void*)sock, 5, NULL);
	}
}

void netaudio_init_i2s(void)
{
	i2s_chan_config_t chan_cfg = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM, I2S_ROLE_MASTER);
	ESP_ERROR_CHECK(i2s_new_channel(&chan_cfg, &tx_handle, NULL));

	i2s_std_config_t std_cfg = {
		.clk_cfg = {
			.sample_rate_hz = I2S_SAMPLE_RATE,
			.clk_src = I2S_CLK_SRC_DEFAULT,
			.ext_clk_freq_hz = 0,
			//.mclk_multiple = I2S_MCLK_MULTIPLE_256,  // 256x for PCM5102: MCLK = fs * 256
			.mclk_multiple = I2S_MCLK_MULTIPLE_384, // 384x for PCM5102 using 24-bit mode... though 192 would probably work?
		},
		.slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO),
		.gpio_cfg = {
			.mclk = I2S_GPIO_UNUSED,  // PCM5102 does not require MCLK
			.bclk = I2S_BCLK_PIN,
			.ws = I2S_WS_PIN,
			.dout = I2S_DOUT_PIN,
			.din = I2S_GPIO_UNUSED,
			.invert_flags = {
				.mclk_inv = false,
				.bclk_inv = false,
				.ws_inv = false,
			},
		},
	};

	ESP_ERROR_CHECK(i2s_channel_init_std_mode(tx_handle, &std_cfg));
	ESP_LOGI(TAG, "I2S initialized for PCM5102 (32-bit, 48kHz, I2S Philips mode, MCLK 384x)");
}

void netaudio_init_netaudio(uint16_t listen_port)
{
	tcp_port = listen_port;

	// initialize the audio subsystem
	netaudio_init_i2s();

	// start a task that will run our TCP server; it accepts connections
	// and starts up audio streams on any inbound connections
	xTaskCreate(tcp_server_task, "tcp_server", 4096, NULL, 5, NULL);
}

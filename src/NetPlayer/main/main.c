#include <stdio.h>
#include <inttypes.h>
#include "sdkconfig.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_chip_info.h"
#include "esp_flash.h"
#include "esp_system.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_ota_ops.h"
#include "nvs_flash.h"
#include "wifi.h"
#include "led.h"
#include "netaudio.h"
#include "config.h"
#include "mdns.h"

#define TAG "NetPlayer"

void app_main(void)
{
	ESP_LOGI(TAG, "NetPlayer starting...");

	// Initialize NVS
	esp_err_t ret = nvs_flash_init();
	if (ret == ESP_ERR_NVS_NO_FREE_PAGES || ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
		ESP_ERROR_CHECK(nvs_flash_erase());
		ret = nvs_flash_init();
	}
	ESP_ERROR_CHECK(ret);

	// create the default event loop
	ESP_ERROR_CHECK(esp_event_loop_create_default());
	ESP_LOGI(TAG, "Default event loop created.");

	// Initialize WiFi
	wifi_init_sta();

	// set up mDNS for .local resolution
	ESP_ERROR_CHECK(mdns_init());
	ESP_ERROR_CHECK(mdns_hostname_set("netplayer"));
	ESP_ERROR_CHECK(mdns_instance_name_set("SongServer NetPlayer"));



	// Initialize RGB LED
	led_init_onboard_rgb();

	// initialize the network audio service
	netaudio_init_netaudio(TCP_PORT);

	// report CPU usage
	//report_cpu_usage();
}


void check_ota(void)
{
	const esp_partition_t *running_partition = esp_ota_get_running_partition();

	if (running_partition != NULL) {
		// Get the app description from the running partition
		esp_app_desc_t app_desc;
		esp_ota_get_partition_description(running_partition, &app_desc);
		
		// Access the firmware version
		ESP_LOGI(TAG, "Current firmware version: %s", app_desc.version);
		
		// Other useful fields:
		// app_desc.project_name - Project name
		// app_desc.idf_version - IDF version used to build
		// app_desc.date - Build date/time
		// app_desc.time - Build time
	}	
}
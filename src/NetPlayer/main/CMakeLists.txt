idf_component_register(SRCS "main.c"
                       PRIV_REQUIRES spi_flash nvs_flash esp_event wifi led netaudio app_update
                       INCLUDE_DIRS "")

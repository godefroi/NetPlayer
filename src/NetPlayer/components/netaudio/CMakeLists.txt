idf_component_register(
    SRCS "netaudio.c"
    INCLUDE_DIRS "include"
    PRIV_REQUIRES lwip freertos esp_driver_i2s esp_timer led
)

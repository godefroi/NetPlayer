#ifndef NETAUDIO_H
#define NETAUDIO_H

#include <stdint.h>

/**
 * @brief Audio task that receives PCM data from socket and writes to I2S
 * 
 * @param pvParameters Socket file descriptor cast to void pointer
 */
void netaudio_init_netaudio(uint16_t listen_port);

#endif // NETAUDIO_H

// config.h — build-time configuration. Every value can be overridden from platformio.ini build_flags,
// e.g. -DLED_PIN=5 -DMAX_LEDS=300 -DLED_TYPE_SK6812_RGBW.

#pragma once

// ---- LED strip -----------------------------------------------------------------------------------------

// Data pin of the strip. Any output-capable GPIO works with RMT; avoid strapping pins (0, 2, 5, 12, 15).
#ifndef LED_PIN
#define LED_PIN 16
#endif

// Largest strip supported. The PC may announce fewer LEDs per frame (the rest stay dark) or more (the
// excess is discarded safely). Must be >= the LED count configured in the app.
#ifndef MAX_LEDS
#define MAX_LEDS 300
#endif

// Exactly one strip type. WS2812B is the default.
#if !defined(LED_TYPE_WS2812B) && !defined(LED_TYPE_SK6812_RGB) && !defined(LED_TYPE_SK6812_RGBW)
#define LED_TYPE_WS2812B
#endif

#if (defined(LED_TYPE_WS2812B) + defined(LED_TYPE_SK6812_RGB) + defined(LED_TYPE_SK6812_RGBW)) != 1
#error "Define exactly one of LED_TYPE_WS2812B, LED_TYPE_SK6812_RGB, LED_TYPE_SK6812_RGBW"
#endif

// ---- Serial --------------------------------------------------------------------------------------------

// Must equal serial.baudRate in the app: 115200, 460800, 921600, 1000000 or 2000000.
// Ignored by native USB-CDC boards (ESP32-S2/S3/C3 with USB CDC on boot), which always run at USB speed.
// Check the USB-UART bridge: CP2102 tops out at 921600, CP2102N at 3000000, CH340 at 2000000.
#ifndef SERIAL_BAUD_RATE
#define SERIAL_BAUD_RATE 1000000
#endif

// Receive ring buffer. At 2 Mbaud, 4096 bytes absorb 20 ms of data, far more than the loop ever stalls,
// so no byte is lost while a frame is being pushed to the strip.
#ifndef SERIAL_RX_BUFFER_SIZE
#define SERIAL_RX_BUFFER_SIZE 4096
#endif

// A frame whose bytes stop arriving for this long is dropped (cable pulled mid-frame).
#ifndef FRAME_STALE_TIMEOUT_MS
#define FRAME_STALE_TIMEOUT_MS 100
#endif

// ---- Watchdog ------------------------------------------------------------------------------------------

// No valid frame for this long means the PC app is gone: start fading out. The app sends keep-alives
// every 500 ms on a static picture, so this never triggers while it runs.
#ifndef WATCHDOG_TIMEOUT_MS
#define WATCHDOG_TIMEOUT_MS 2000
#endif

// Duration of the fade to black once the watchdog has fired.
#ifndef FADE_DURATION_MS
#define FADE_DURATION_MS 1000
#endif

// Refresh period while fading (50 Hz is perfectly smooth for a slow fade).
#ifndef FADE_STEP_MS
#define FADE_STEP_MS 20
#endif

// While idle, announce "Ada\n" this often so host tools that auto-detect Adalight devices find the board.
#ifndef GREETING_INTERVAL_MS
#define GREETING_INTERVAL_MS 1000
#endif

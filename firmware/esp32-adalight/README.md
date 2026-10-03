# ESP32 Adalight receiver

Firmware for the LED controller of System-wide Ambient Light: receives Adalight frames over USB serial
and drives a WS2812B / SK6812 strip through the ESP32's RMT peripheral.

## Wiring

```
PC ── USB ── ESP32 DevKit ── GPIO16 ──[330 Ω]── DIN  WS2812B / SK6812 strip
                  GND ───────────────────────── GND
     5 V supply ─────────────────────────────── 5V   (+ 1000 µF across 5V/GND at the strip input)
```

- Connect the strip GND to the ESP32 GND, always.
- WS2812B usually reads the ESP32's 3.3 V data reliably over a short wire. For long wires or a flaky
  first LED, add a 74AHCT125 level shifter.
- Up to ~40 LEDs can be powered from USB. The app's power limiter defaults to 400 mA for that case;
  with a dedicated supply, raise `serial.maxCurrentMilliamps` to ~80 % of its rating.

## Build and flash

Install [PlatformIO](https://platformio.org/install/cli), then from this folder:

| Board | Command |
|---|---|
| ESP32 DevKit (CP2102 / CH340 USB-UART) | `pio run -e esp32dev -t upload` |
| ESP32-S3 DevKitC, native USB port | `pio run -e esp32-s3 -t upload` |
| Unit tests on the PC (no hardware) | `pio test -e native` |

Configuration lives in `include/config.h`; override any value from `platformio.ini` without editing it:

```ini
[env:esp32dev]
build_flags = ${esp32_common.build_flags} -DLED_PIN=5 -DMAX_LEDS=120 -DLED_TYPE_SK6812_RGBW -DSERIAL_BAUD_RATE=2000000
```

| Setting | Default | Notes |
|---|---|---|
| `LED_PIN` | 16 | Avoid strapping pins 0, 2, 5, 12, 15 |
| `MAX_LEDS` | 300 | Must be ≥ the LED count configured in the app |
| `LED_TYPE_*` | `WS2812B` | `LED_TYPE_SK6812_RGB` or `LED_TYPE_SK6812_RGBW` (white channel extracted automatically) |
| `SERIAL_BAUD_RATE` | 1000000 | Must equal `serial.baudRate` in the app. Bridge limits: CP2102 921600, CH340 2000000, CP2102N 3000000. Ignored on native USB (S2/S3/C3) |
| `WATCHDOG_TIMEOUT_MS` | 2000 | No frame for this long → fade out |
| `FADE_DURATION_MS` | 1000 | Length of the fade to black |

## How it works

| Concern | Solution |
|---|---|
| Strip output must not block serial reception | The RMT peripheral generates the WS2812 waveform in hardware, so interrupts stay enabled and the UART keeps receiving during a transfer. `Show()` is only called when `CanShow()` says the previous transfer has finished, so `loop()` never waits. |
| No byte loss at 2 Mbaud | 4 KB receive ring buffer (20 ms of data at 2 Mbaud), drained in 256-byte bulk reads. |
| Corrupt or misaligned headers | Header checksum (`hi ^ lo ^ 0x55`); the magic search restarts on every `A`. |
| Oversized frames | Accepted and fully consumed to stay in sync, but only `MAX_LEDS` are stored: no write past the buffer. |
| Cable pulled mid-frame | A partial frame older than 100 ms is discarded, so its tail is never stitched onto the next frame. |
| App closed / PC asleep | No valid frame for 2 s → quadratic fade to black over 1 s (looks even to the eye); the next frame restores full brightness at once. The app re-sends the last frame every 500 ms on a static picture, so this never fires while it runs. |
| `millis()` wrap after 49 days | All time comparisons use unsigned subtraction; covered by tests. |
| Host auto-detection | While idle, the board prints `Ada\n` once per second, the Adalight greeting. |

`lib/AdalightCore` holds the protocol parser and watchdog as plain C++17 without Arduino dependencies,
which is what lets `pio test -e native` exercise them on the PC. Its golden vector
(`41 64 61 00 02 57 FF 00 00 00 FF 00 00 00 FF`) is the same one the C# encoder is tested against.

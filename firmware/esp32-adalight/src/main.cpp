// AmbientLight ESP32 Adalight receiver.
//
// Data path:
//   UART RX ISR ──► 4 KB ring buffer ──► loop(): bulk read ──► AdalightParser ──► NeoPixelBus (RMT) ──► strip
//
// Why the strip uses the RMT peripheral: WS2812B/SK6812 need a precisely timed 800 kHz waveform. A
// bit-banged driver (as on AVR Arduinos) must disable interrupts for the whole transfer — about 9 ms for
// 300 LEDs — during which the UART receive interrupt cannot run and bytes arriving at 1-2 Mbaud are lost.
// The RMT peripheral generates the waveform in hardware from a buffer, so interrupts stay enabled, the UART
// keeps filling the ring buffer, and loop() never blocks on the strip: it only calls Show() when
// CanShow() reports that the previous transfer has finished.

#include <Arduino.h>
#include <NeoPixelBus.h>

#include "AdalightParser.h"
#include "IdleFader.h"
#include "config.h"

#if defined(LED_TYPE_WS2812B)
using StripFeature = NeoGrbFeature;
using StripMethod = NeoEsp32Rmt0Ws2812xMethod;
#elif defined(LED_TYPE_SK6812_RGB)
using StripFeature = NeoGrbFeature;
using StripMethod = NeoEsp32Rmt0Sk6812Method;
#elif defined(LED_TYPE_SK6812_RGBW)
using StripFeature = NeoGrbwFeature;
using StripMethod = NeoEsp32Rmt0Sk6812Method;
#endif

namespace {

constexpr size_t kFrameBytes = static_cast<size_t>(MAX_LEDS) * 3;
constexpr size_t kReadChunk = 256;

// Bytes handled per loop() pass before the strip gets a chance to refresh: bounded so a continuous
// stream cannot starve Show(), and larger than one full frame of a 300-LED strip.
constexpr size_t kMaxBytesPerPass = 4096;

static_assert(MAX_LEDS > 0 && MAX_LEDS <= 65535, "MAX_LEDS must be 1..65535");

uint8_t parserBuffer[kFrameBytes];
uint8_t lastFrame[kFrameBytes];
uint8_t readChunk[kReadChunk];

adalight::Parser parser(parserBuffer, MAX_LEDS, FRAME_STALE_TIMEOUT_MS);
adalight::IdleFader fader(WATCHDOG_TIMEOUT_MS, FADE_DURATION_MS);
NeoPixelBus<StripFeature, StripMethod> strip(MAX_LEDS, LED_PIN);

uint16_t lastFrameLeds = 0;
uint8_t appliedLevel = 0;
bool showPending = false;
uint32_t lastFadeUpdateMs = 0;
uint32_t lastGreetingMs = 0;

void setPixel(uint16_t index, uint8_t r, uint8_t g, uint8_t b) {
#if defined(LED_TYPE_SK6812_RGBW)
    const adalight::Rgbw color = adalight::extractWhite(r, g, b);
    strip.SetPixelColor(index, RgbwColor(color.r, color.g, color.b, color.w));
#else
    strip.SetPixelColor(index, RgbColor(r, g, b));
#endif
}

// Copies the last received frame into the strip buffer at `level` (255 = as received).
void applyLastFrame(uint8_t level) {
    for (uint16_t i = 0; i < lastFrameLeds; ++i) {
        const uint8_t* rgb = &lastFrame[static_cast<size_t>(i) * 3];
        setPixel(i,
                 adalight::scale8(rgb[0], level),
                 adalight::scale8(rgb[1], level),
                 adalight::scale8(rgb[2], level));
    }

    // LEDs beyond the frame (the app is configured for a shorter strip) stay dark.
    for (uint16_t i = lastFrameLeds; i < MAX_LEDS; ++i) {
        setPixel(i, 0, 0, 0);
    }

    appliedLevel = level;
    showPending = true;
}

// Drains the UART ring buffer through the parser. Returns true if at least one complete frame arrived;
// when several arrive in one pass only the newest is kept (latest wins, like on the PC side).
bool receiveFrames(uint32_t nowMs) {
    bool frameArrived = false;
    size_t handled = 0;

    while (handled < kMaxBytesPerPass) {
        const int available = Serial.available();
        if (available <= 0) {
            break;
        }

        const size_t wanted = static_cast<size_t>(available) < kReadChunk ? static_cast<size_t>(available) : kReadChunk;
        const size_t count = Serial.read(readChunk, wanted);
        if (count == 0) {
            break;
        }

        for (size_t i = 0; i < count; ++i) {
            if (parser.feed(readChunk[i], nowMs) == adalight::FeedResult::FrameComplete) {
                lastFrameLeds = parser.pixelCount();
                memcpy(lastFrame, parser.pixels(), static_cast<size_t>(lastFrameLeds) * 3);
                fader.frameReceived(nowMs);
                frameArrived = true;
            }
        }

        handled += count;
    }

    return frameArrived;
}

} // namespace

void setup() {
    // The ring buffer must be sized before begin().
    Serial.setRxBufferSize(SERIAL_RX_BUFFER_SIZE);
    Serial.begin(SERIAL_BAUD_RATE);

    strip.Begin();
    strip.ClearTo(0);
    strip.Show();

    Serial.print("Ada\n");
    lastGreetingMs = millis();
}

void loop() {
    const uint32_t now = millis();

    if (receiveFrames(now)) {
        applyLastFrame(255);
    } else {
        // Watchdog: no frame for WATCHDOG_TIMEOUT_MS -> fade out over FADE_DURATION_MS, then stay dark.
        const uint8_t level = fader.level(now);
        if (level != appliedLevel && static_cast<uint32_t>(now - lastFadeUpdateMs) >= FADE_STEP_MS) {
            applyLastFrame(level);
            lastFadeUpdateMs = now;
        }

        // Announce ourselves while nobody is talking to us (host-side auto-detection).
        if (fader.idleMs(now) >= GREETING_INTERVAL_MS || lastFrameLeds == 0) {
            if (static_cast<uint32_t>(now - lastGreetingMs) >= GREETING_INTERVAL_MS) {
                Serial.print("Ada\n");
                lastGreetingMs = now;
            }
        }
    }

    // Never wait for the strip: if the previous RMT transfer is still running, try again next pass.
    if (showPending && strip.CanShow()) {
        strip.Show();
        showPending = false;
    }
}

// IdleFader.h — the "host went away" watchdog.
//
// The PC sends a frame (or a keep-alive repeat of the last one) at least every 500 ms while the app runs.
// If nothing valid arrives for `timeoutMs` (2 s), the app was closed, crashed or the PC went to sleep:
// the strip fades out over `fadeMs` instead of freezing on the last color forever. Any new frame
// restores full brightness immediately.
//
// The fade is quadratic in PWM duty, which looks even to the eye (perceived brightness is far from
// linear in duty: a linear fade seems to linger and then drop off suddenly at the end).
//
// Pure C++17, no Arduino dependency.

#pragma once

#include <stdint.h>

namespace adalight {

class IdleFader {
public:
    IdleFader(uint32_t timeoutMs, uint32_t fadeMs) : timeoutMs_(timeoutMs), fadeMs_(fadeMs) {}

    void frameReceived(uint32_t nowMs) {
        lastFrameMs_ = nowMs;
        hasFrame_ = true;
    }

    // 255 = show the last frame as is, 0 = strip off. Wrap-around safe.
    uint8_t level(uint32_t nowMs) const {
        if (!hasFrame_) {
            return 0;
        }

        const uint32_t age = nowMs - lastFrameMs_;
        if (age <= timeoutMs_) {
            return 255;
        }

        const uint32_t fading = age - timeoutMs_;
        if (fadeMs_ == 0 || fading >= fadeMs_) {
            return 0;
        }

        // remaining in 0..65535 fixed point (64-bit so any fade length is safe), squared back to 0..255.
        const uint64_t remaining = (static_cast<uint64_t>(fadeMs_ - fading) * 65535u) / fadeMs_;
        return static_cast<uint8_t>((remaining * remaining) / (65535ull * 257ull));
    }

    // True once the fade has completed (or before the first frame).
    bool isDark(uint32_t nowMs) const { return level(nowMs) == 0; }

    // Milliseconds since the last valid frame (0 before the first one).
    uint32_t idleMs(uint32_t nowMs) const { return hasFrame_ ? nowMs - lastFrameMs_ : 0; }

private:
    uint32_t timeoutMs_;
    uint32_t fadeMs_;
    uint32_t lastFrameMs_ = 0;
    bool hasFrame_ = false;
};

// Scales an 8-bit channel by an 8-bit level; exact at both ends (level 255 keeps the value, 0 gives 0).
inline uint8_t scale8(uint8_t value, uint8_t level) {
    return static_cast<uint8_t>((static_cast<uint16_t>(value) * (static_cast<uint16_t>(level) + 1u)) >> 8);
}

struct Rgbw {
    uint8_t r;
    uint8_t g;
    uint8_t b;
    uint8_t w;
};

// RGB to RGBW for SK6812 RGBW strips: the grey part common to all three channels moves to the dedicated
// white LED (more efficient and a cleaner white), the remainder stays colored. Lossless: r = r' + w, etc.
inline Rgbw extractWhite(uint8_t r, uint8_t g, uint8_t b) {
    uint8_t w = r < g ? r : g;
    w = w < b ? w : b;
    return Rgbw{static_cast<uint8_t>(r - w), static_cast<uint8_t>(g - w), static_cast<uint8_t>(b - w), w};
}

} // namespace adalight

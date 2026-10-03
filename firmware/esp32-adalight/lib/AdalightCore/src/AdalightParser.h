// AdalightParser.h — byte-at-a-time Adalight frame parser.
//
// Frame layout (identical to AmbientLight.Serial.Protocol.AdalightEncoder on the PC side):
//
//   'A' 'd' 'a' count-hi count-lo checksum  R G B  R G B  ...
//   count = LED count - 1, checksum = count-hi ^ count-lo ^ 0x55
//
// Safety properties:
//   - No heap: the caller provides a fixed buffer of `capacityLeds` * 3 bytes. A header announcing more
//     LEDs than fit is still accepted and its payload fully consumed (to stay in sync), but bytes beyond
//     the buffer are discarded and counted, never written.
//   - Header checksum: a header assembled from noise or misaligned bytes is rejected and the parser goes
//     back to searching for "Ada".
//   - Resynchronization: the magic search restarts on every 'A', so "AAda" or "AdAda" still lock on.
//   - Stale frames: if bytes stop arriving in the middle of a frame (USB unplugged, host crashed), the
//     partial frame is dropped after `staleTimeoutMs`, so its tail can never be stitched to the next frame.
//
// Pure C++17, no Arduino dependency: built for the ESP32 and for the host unit tests alike.

#pragma once

#include <stddef.h>
#include <stdint.h>

namespace adalight {

constexpr uint8_t kChecksumSeed = 0x55;
constexpr size_t kHeaderLength = 6;

enum class FeedResult : uint8_t {
    None,           // byte consumed, nothing to report
    FrameComplete,  // a full frame is available through pixels()/pixelCount()
    HeaderRejected, // a header failed its checksum and was discarded
};

struct ParserStats {
    uint32_t framesCompleted = 0;
    uint32_t headersRejected = 0;
    uint32_t staleResets = 0;
    uint32_t droppedLeds = 0; // LEDs announced beyond the buffer capacity, summed over frames
};

class Parser {
public:
    Parser(uint8_t* frameBuffer, uint16_t capacityLeds, uint32_t staleTimeoutMs);

    // Consumes one received byte. `nowMs` is a millisecond clock (wrap-around safe).
    FeedResult feed(uint8_t value, uint32_t nowMs);

    // Valid after FeedResult::FrameComplete until the next call to feed().
    const uint8_t* pixels() const { return buffer_; }
    uint16_t pixelCount() const { return storedLeds_; }
    uint32_t announcedLedCount() const { return announcedLeds_; }

    const ParserStats& stats() const { return stats_; }
    bool inFrame() const { return state_ != State::MagicA; }

    // Drops any partial frame and starts searching for a header again.
    void reset();

private:
    enum class State : uint8_t { MagicA, MagicD, MagicA2, CountHigh, CountLow, Checksum, Payload };

    void restartMagic(uint8_t value);

    uint8_t* const buffer_;
    const uint32_t capacityBytes_;
    const uint32_t staleTimeoutMs_;

    State state_ = State::MagicA;
    uint8_t countHigh_ = 0;
    uint8_t countLow_ = 0;
    uint32_t announcedLeds_ = 0;
    uint32_t payloadBytes_ = 0;
    uint32_t received_ = 0;
    uint16_t storedLeds_ = 0;
    uint32_t lastByteMs_ = 0;
    ParserStats stats_;
};

} // namespace adalight

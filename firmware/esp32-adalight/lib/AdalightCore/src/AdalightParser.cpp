#include "AdalightParser.h"

namespace adalight {

Parser::Parser(uint8_t* frameBuffer, uint16_t capacityLeds, uint32_t staleTimeoutMs)
    : buffer_(frameBuffer),
      capacityBytes_(static_cast<uint32_t>(capacityLeds) * 3u),
      staleTimeoutMs_(staleTimeoutMs) {}

void Parser::reset() {
    state_ = State::MagicA;
    received_ = 0;
}

void Parser::restartMagic(uint8_t value) {
    // A mismatching byte may itself be the first byte of the next header.
    state_ = value == 'A' ? State::MagicD : State::MagicA;
}

FeedResult Parser::feed(uint8_t value, uint32_t nowMs) {
    // Unsigned subtraction is correct across the 49-day millis() wrap-around.
    if (state_ != State::MagicA && static_cast<uint32_t>(nowMs - lastByteMs_) > staleTimeoutMs_) {
        reset();
        ++stats_.staleResets;
    }
    lastByteMs_ = nowMs;

    switch (state_) {
    case State::MagicA:
        if (value == 'A') {
            state_ = State::MagicD;
        }
        return FeedResult::None;

    case State::MagicD:
        if (value == 'd') {
            state_ = State::MagicA2;
        } else {
            restartMagic(value);
        }
        return FeedResult::None;

    case State::MagicA2:
        if (value == 'a') {
            state_ = State::CountHigh;
        } else {
            restartMagic(value);
        }
        return FeedResult::None;

    case State::CountHigh:
        countHigh_ = value;
        state_ = State::CountLow;
        return FeedResult::None;

    case State::CountLow:
        countLow_ = value;
        state_ = State::Checksum;
        return FeedResult::None;

    case State::Checksum: {
        const uint8_t expected = static_cast<uint8_t>(countHigh_ ^ countLow_ ^ kChecksumSeed);
        if (value != expected) {
            ++stats_.headersRejected;
            restartMagic(value);
            return FeedResult::HeaderRejected;
        }

        announcedLeds_ = ((static_cast<uint32_t>(countHigh_) << 8) | countLow_) + 1u;
        payloadBytes_ = announcedLeds_ * 3u;
        received_ = 0;
        state_ = State::Payload;
        return FeedResult::None;
    }

    case State::Payload:
        if (received_ < capacityBytes_) {
            buffer_[received_] = value;
        }
        ++received_;

        if (received_ < payloadBytes_) {
            return FeedResult::None;
        }

        {
            const uint32_t capacityLeds = capacityBytes_ / 3u;
            const uint32_t stored = announcedLeds_ < capacityLeds ? announcedLeds_ : capacityLeds;
            storedLeds_ = static_cast<uint16_t>(stored);
            stats_.droppedLeds += announcedLeds_ - stored;
        }
        ++stats_.framesCompleted;
        state_ = State::MagicA;
        return FeedResult::FrameComplete;
    }

    return FeedResult::None;
}

} // namespace adalight

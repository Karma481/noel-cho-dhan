// Host-side unit tests for the firmware's protocol parser and watchdog: `pio test -e native`.
// The golden vectors are the same as in AmbientLight.Serial.Tests (C#), so both ends of the wire are
// verified against identical bytes.

#include <unity.h>

#include <stdint.h>
#include <string.h>

#include <vector>

#include "AdalightParser.h"
#include "IdleFader.h"

using adalight::FeedResult;
using adalight::IdleFader;
using adalight::Parser;

namespace {

constexpr uint32_t kStaleMs = 100;

// Three LEDs red, green, blue: count-1 = 2 -> hi 0x00, lo 0x02, checksum 0x00 ^ 0x02 ^ 0x55 = 0x57.
const uint8_t kGoldenRgb[] = {
    'A', 'd', 'a', 0x00, 0x02, 0x57,
    0xFF, 0x00, 0x00,
    0x00, 0xFF, 0x00,
    0x00, 0x00, 0xFF,
};

std::vector<uint8_t> encode(const std::vector<uint8_t>& rgb) {
    const uint32_t count = static_cast<uint32_t>(rgb.size() / 3) - 1;
    const uint8_t hi = static_cast<uint8_t>(count >> 8);
    const uint8_t lo = static_cast<uint8_t>(count);
    std::vector<uint8_t> frame = {'A', 'd', 'a', hi, lo, static_cast<uint8_t>(hi ^ lo ^ 0x55)};
    frame.insert(frame.end(), rgb.begin(), rgb.end());
    return frame;
}

// Feeds bytes and returns how many frames completed.
int feedAll(Parser& parser, const uint8_t* data, size_t length, uint32_t nowMs = 0) {
    int frames = 0;
    for (size_t i = 0; i < length; ++i) {
        if (parser.feed(data[i], nowMs) == FeedResult::FrameComplete) {
            ++frames;
        }
    }
    return frames;
}

int feedAll(Parser& parser, const std::vector<uint8_t>& data, uint32_t nowMs = 0) {
    return feedAll(parser, data.data(), data.size(), nowMs);
}

} // namespace

void setUp() {}
void tearDown() {}

void test_golden_frame_decodes() {
    uint8_t buffer[10 * 3] = {};
    Parser parser(buffer, 10, kStaleMs);

    TEST_ASSERT_EQUAL_INT(1, feedAll(parser, kGoldenRgb, sizeof kGoldenRgb));
    TEST_ASSERT_EQUAL_UINT16(3, parser.pixelCount());
    TEST_ASSERT_EQUAL_UINT8_ARRAY(kGoldenRgb + 6, parser.pixels(), 9);
}

void test_checksums_match_the_encoder_for_every_count() {
    // Same header formula as the C# AdalightEncoder, for all 65536 counts.
    for (uint32_t leds = 1; leds <= 65536; ++leds) {
        const uint32_t count = leds - 1;
        const uint8_t hi = static_cast<uint8_t>(count >> 8);
        const uint8_t lo = static_cast<uint8_t>(count);
        uint8_t buffer[3] = {};
        Parser parser(buffer, 1, kStaleMs);
        const uint8_t header[] = {'A', 'd', 'a', hi, lo, static_cast<uint8_t>(hi ^ lo ^ 0x55)};
        for (uint8_t byte : header) {
            TEST_ASSERT_TRUE(parser.feed(byte, 0) != FeedResult::HeaderRejected);
        }
        TEST_ASSERT_EQUAL_UINT32(leds, parser.announcedLedCount());
    }
}

void test_bad_checksum_is_rejected_and_parser_resyncs() {
    uint8_t buffer[3 * 3] = {};
    Parser parser(buffer, 3, kStaleMs);
    const uint8_t corrupt[] = {'A', 'd', 'a', 0x00, 0x02, 0x58};

    for (size_t i = 0; i < sizeof corrupt - 1; ++i) {
        parser.feed(corrupt[i], 0);
    }
    TEST_ASSERT_EQUAL(static_cast<int>(FeedResult::HeaderRejected), static_cast<int>(parser.feed(corrupt[5], 0)));
    TEST_ASSERT_EQUAL_UINT32(1, parser.stats().headersRejected);

    TEST_ASSERT_EQUAL_INT(1, feedAll(parser, kGoldenRgb, sizeof kGoldenRgb));
    TEST_ASSERT_EQUAL_UINT8_ARRAY(kGoldenRgb + 6, parser.pixels(), 9);
}

void test_garbage_and_partial_magic_before_a_frame() {
    uint8_t buffer[3 * 3] = {};
    Parser parser(buffer, 3, kStaleMs);
    // Noise, a lone 'A', "Ad" without 'a', then "AAda" (restart on the second 'A').
    const uint8_t noise[] = {0x00, 0xFF, 'A', 0x10, 'A', 'd', 0x41, 'x', 'A'};

    TEST_ASSERT_EQUAL_INT(0, feedAll(parser, noise, sizeof noise));
    TEST_ASSERT_EQUAL_INT(1, feedAll(parser, kGoldenRgb, sizeof kGoldenRgb));
    TEST_ASSERT_EQUAL_UINT8_ARRAY(kGoldenRgb + 6, parser.pixels(), 9);
}

void test_back_to_back_frames_all_complete() {
    uint8_t buffer[100 * 3] = {};
    Parser parser(buffer, 100, kStaleMs);
    std::vector<uint8_t> stream;
    for (int frame = 0; frame < 50; ++frame) {
        std::vector<uint8_t> rgb(100 * 3, static_cast<uint8_t>(frame));
        const auto encoded = encode(rgb);
        stream.insert(stream.end(), encoded.begin(), encoded.end());
    }

    TEST_ASSERT_EQUAL_INT(50, feedAll(parser, stream));
    TEST_ASSERT_EQUAL_UINT8(49, parser.pixels()[0]);
    TEST_ASSERT_EQUAL_UINT8(49, parser.pixels()[299]);
}

void test_oversized_frame_never_writes_past_the_buffer_and_stays_in_sync() {
    // Guard bytes around a 2-LED buffer catch any overflow.
    uint8_t memory[4 + 2 * 3 + 4];
    memset(memory, 0xEE, sizeof memory);
    uint8_t* buffer = memory + 4;
    Parser parser(buffer, 2, kStaleMs);

    std::vector<uint8_t> rgb(1000 * 3);
    for (size_t i = 0; i < rgb.size(); ++i) {
        rgb[i] = static_cast<uint8_t>(i);
    }

    TEST_ASSERT_EQUAL_INT(1, feedAll(parser, encode(rgb)));
    TEST_ASSERT_EQUAL_UINT16(2, parser.pixelCount());
    TEST_ASSERT_EQUAL_UINT32(998, parser.stats().droppedLeds);
    for (int i = 0; i < 4; ++i) {
        TEST_ASSERT_EQUAL_HEX8(0xEE, memory[i]);
        TEST_ASSERT_EQUAL_HEX8(0xEE, memory[4 + 6 + i]);
    }

    // Still aligned: the next normal frame decodes.
    TEST_ASSERT_EQUAL_INT(1, feedAll(parser, kGoldenRgb, sizeof kGoldenRgb));
}

void test_stale_partial_frame_is_dropped() {
    uint8_t buffer[3 * 3] = {};
    Parser parser(buffer, 3, kStaleMs);

    // Header plus half the payload at t = 0, then the cable is pulled.
    TEST_ASSERT_EQUAL_INT(0, feedAll(parser, kGoldenRgb, 10, 0));
    TEST_ASSERT_TRUE(parser.inFrame());

    // 500 ms later a complete new frame arrives; its bytes must not finish the old one.
    TEST_ASSERT_EQUAL_INT(1, feedAll(parser, kGoldenRgb, sizeof kGoldenRgb, 500));
    TEST_ASSERT_EQUAL_UINT32(1, parser.stats().staleResets);
    TEST_ASSERT_EQUAL_UINT8_ARRAY(kGoldenRgb + 6, parser.pixels(), 9);
}

void test_lost_bytes_cost_at_most_the_damaged_frame() {
    uint8_t buffer[3 * 3] = {};
    Parser parser(buffer, 3, kStaleMs);
    std::vector<uint8_t> stream(kGoldenRgb, kGoldenRgb + sizeof kGoldenRgb);
    stream.erase(stream.begin() + 8); // one payload byte lost on the wire
    for (int i = 0; i < 3; ++i) {
        stream.insert(stream.end(), kGoldenRgb, kGoldenRgb + sizeof kGoldenRgb);
    }

    // The damaged frame swallows the next header as payload, then the parser resyncs.
    const int frames = feedAll(parser, stream);
    TEST_ASSERT_TRUE(frames >= 2);
    TEST_ASSERT_EQUAL_UINT8_ARRAY(kGoldenRgb + 6, parser.pixels(), 9);
}

void test_stale_check_survives_millis_wraparound() {
    uint8_t buffer[3 * 3] = {};
    Parser parser(buffer, 3, kStaleMs);
    const uint32_t beforeWrap = 0xFFFFFFF0u;

    feedAll(parser, kGoldenRgb, 8, beforeWrap);
    // 32 ms later the clock has wrapped to 0x10: not stale, the frame completes.
    TEST_ASSERT_EQUAL_INT(1, feedAll(parser, kGoldenRgb + 8, sizeof kGoldenRgb - 8, 0x10u));
    TEST_ASSERT_EQUAL_UINT32(0, parser.stats().staleResets);
}

void test_fader_is_dark_before_the_first_frame() {
    IdleFader fader(2000, 1000);
    TEST_ASSERT_EQUAL_UINT8(0, fader.level(12345));
}

void test_fader_holds_full_brightness_until_the_watchdog_fires() {
    IdleFader fader(2000, 1000);
    fader.frameReceived(1000);

    TEST_ASSERT_EQUAL_UINT8(255, fader.level(1000));
    TEST_ASSERT_EQUAL_UINT8(255, fader.level(3000)); // exactly 2 s
    TEST_ASSERT_TRUE(fader.level(3001) < 255);
}

void test_fader_fades_monotonically_to_black_within_the_fade_time() {
    IdleFader fader(2000, 1000);
    fader.frameReceived(0);

    uint8_t previous = 255;
    for (uint32_t t = 2000; t <= 3000; t += 10) {
        const uint8_t level = fader.level(t);
        TEST_ASSERT_TRUE(level <= previous);
        previous = level;
    }
    TEST_ASSERT_EQUAL_UINT8(0, fader.level(3000));
    TEST_ASSERT_TRUE(fader.isDark(60000));

    // Quadratic: half-way through the fade the duty is about a quarter.
    const uint8_t midway = fader.level(2500);
    TEST_ASSERT_UINT8_WITHIN(3, 64, midway);
}

void test_fader_recovers_instantly_on_a_new_frame() {
    IdleFader fader(2000, 1000);
    fader.frameReceived(0);
    TEST_ASSERT_EQUAL_UINT8(0, fader.level(5000));

    fader.frameReceived(5000);
    TEST_ASSERT_EQUAL_UINT8(255, fader.level(5001));
}

void test_fader_survives_millis_wraparound() {
    IdleFader fader(2000, 1000);
    fader.frameReceived(0xFFFFFF00u);
    TEST_ASSERT_EQUAL_UINT8(255, fader.level(0x00000100u)); // 512 ms later, after the wrap
}

void test_scale8_is_exact_at_both_ends() {
    for (int value = 0; value <= 255; ++value) {
        TEST_ASSERT_EQUAL_UINT8(value, adalight::scale8(static_cast<uint8_t>(value), 255));
        TEST_ASSERT_EQUAL_UINT8(0, adalight::scale8(static_cast<uint8_t>(value), 0));
    }
    TEST_ASSERT_EQUAL_UINT8(128, adalight::scale8(255, 128));
}

void test_white_extraction_is_lossless() {
    for (int r = 0; r <= 255; r += 15) {
        for (int g = 0; g <= 255; g += 17) {
            for (int b = 0; b <= 255; b += 51) {
                const auto c = adalight::extractWhite(static_cast<uint8_t>(r), static_cast<uint8_t>(g), static_cast<uint8_t>(b));
                TEST_ASSERT_EQUAL_INT(r, c.r + c.w);
                TEST_ASSERT_EQUAL_INT(g, c.g + c.w);
                TEST_ASSERT_EQUAL_INT(b, c.b + c.w);
                TEST_ASSERT_TRUE(c.r == 0 || c.g == 0 || c.b == 0);
            }
        }
    }
}

int main(int, char**) {
    UNITY_BEGIN();
    RUN_TEST(test_golden_frame_decodes);
    RUN_TEST(test_checksums_match_the_encoder_for_every_count);
    RUN_TEST(test_bad_checksum_is_rejected_and_parser_resyncs);
    RUN_TEST(test_garbage_and_partial_magic_before_a_frame);
    RUN_TEST(test_back_to_back_frames_all_complete);
    RUN_TEST(test_oversized_frame_never_writes_past_the_buffer_and_stays_in_sync);
    RUN_TEST(test_stale_partial_frame_is_dropped);
    RUN_TEST(test_lost_bytes_cost_at_most_the_damaged_frame);
    RUN_TEST(test_stale_check_survives_millis_wraparound);
    RUN_TEST(test_fader_is_dark_before_the_first_frame);
    RUN_TEST(test_fader_holds_full_brightness_until_the_watchdog_fires);
    RUN_TEST(test_fader_fades_monotonically_to_black_within_the_fade_time);
    RUN_TEST(test_fader_recovers_instantly_on_a_new_frame);
    RUN_TEST(test_fader_survives_millis_wraparound);
    RUN_TEST(test_scale8_is_exact_at_both_ends);
    RUN_TEST(test_white_extraction_is_lossless);
    return UNITY_END();
}

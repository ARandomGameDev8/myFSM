#include "binary_format.hpp"

namespace fsmc::fmt {

// ---------------------------------------------------------------------------
// explicit little-endian writers
// ---------------------------------------------------------------------------

void writeU8(std::vector<uint8_t>& out, uint8_t v) { out.push_back(v); }

void writeU16(std::vector<uint8_t>& out, uint16_t v) {
    out.push_back(uint8_t(v & 0xFF));
    out.push_back(uint8_t((v >> 8) & 0xFF));
}

void writeU32(std::vector<uint8_t>& out, uint32_t v) {
    out.push_back(uint8_t(v & 0xFF));
    out.push_back(uint8_t((v >> 8) & 0xFF));
    out.push_back(uint8_t((v >> 16) & 0xFF));
    out.push_back(uint8_t((v >> 24) & 0xFF));
}

void writeU64(std::vector<uint8_t>& out, uint64_t v) {
    writeU32(out, uint32_t(v & 0xFFFFFFFFu));
    writeU32(out, uint32_t((v >> 32) & 0xFFFFFFFFu));
}

namespace {
union FloatBits {
    float f;
    uint32_t u;
};
union DoubleBits {
    double d;
    uint64_t u;
};
} // namespace

void writeFloat(std::vector<uint8_t>& out, float v) {
    FloatBits cv;
    cv.f = v;
    writeU32(out, cv.u);
}

void writeDouble(std::vector<uint8_t>& out, double v) {
    DoubleBits cv;
    cv.d = v;
    writeU64(out, cv.u);
}

void writeBytes(std::vector<uint8_t>& out, const uint8_t* p, std::size_t n) {
    out.insert(out.end(), p, p + n);
}

void writeString(std::vector<uint8_t>& out, const std::string& s) {
    writeU16(out, uint16_t(s.size()));
    writeBytes(out, reinterpret_cast<const uint8_t*>(s.data()), s.size());
}

uint32_t floatToBits(float f) {
    FloatBits cv;
    cv.f = f;
    return cv.u;
}

float bitsToFloat(uint32_t u) {
    FloatBits cv;
    cv.u = u;
    return cv.f;
}

uint64_t doubleToBits(double d) {
    DoubleBits cv;
    cv.d = d;
    return cv.u;
}

double bitsToDouble(uint64_t u) {
    DoubleBits cv;
    cv.u = u;
    return cv.d;
}

} // namespace fsmc::fmt

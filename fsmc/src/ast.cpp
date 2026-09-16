#include "ast.hpp"

namespace fsmc {

std::vector<uint8_t> ConstValue::toBytes() const {
    std::vector<uint8_t> out;
    if (!type) return out;
    switch (type->typeTag) {
        case 0x01: // int
            fmt::writeU32(out, uint32_t(i));
            break;
        case 0x02: // float
            fmt::writeFloat(out, f);
            break;
        case 0x03: // double
            fmt::writeDouble(out, d);
            break;
        case 0x04: // bool
            fmt::writeU8(out, b ? 1 : 0);
            break;
        case 0x05: // string — [4] byte length + UTF-8 bytes (variable width)
            fmt::writeU32(out, uint32_t(str.size()));
            fmt::writeBytes(out, reinterpret_cast<const uint8_t*>(str.data()), str.size());
            break;
        case 0x10: // Vector2
        case 0x11: // Vector3
        case 0x12: // Quaternion
            for (int k = 0; k < vcount; ++k) fmt::writeFloat(out, v[k]);
            break;
        default:
            break; // handles and void have no in-line literal bytes
    }
    return out;
}

} // namespace fsmc

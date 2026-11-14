#include "flydigi/Apex4XInputProtocol.h"
#include "flydigi/Apex4Protocol.h"
#include <algorithm>
#include <cwctype>

namespace asb::flydigi {
bool isApex4XInputCandidate(const HidDeviceInfo& info) {
    if (info.vendorId != 0x045E || info.productId != 0x028E ||
        info.usagePage != 1 || info.usage != 5) return false;
    auto product = info.product;
    std::transform(product.begin(), product.end(), product.begin(),
                   [](wchar_t c) { return static_cast<wchar_t>(std::towlower(c)); });
    return product.find(L"flydigi") != std::wstring::npos;
}

std::optional<std::array<std::uint8_t, 15>> translateApex4XInputCommand(
    std::span<const std::uint8_t> input) {
    if (input.size() < 2 || input[0] != kApex4CommandReportId) return std::nullopt;
    std::array<std::uint8_t, 15> result{};
    result[0] = 0xA5;
    if (input[1] == kApex4CmdGetInfo && input.size() == kApex4IdentityRequestSize) {
        result[1] = 0x10;
        result[2] = 0x42;
        return result;
    }
    if (input[1] == kApex4CmdSetForceTriggerDInput &&
        input.size() == kApex4ForceTriggerReportSize &&
        input[2] == kApex4ForceTriggerEffectFamily && input[3] == 0 &&
        input[4] >= 1 && input[4] <= 3 && input[5] <= 5) {
        result[1] = 0x30;
        result[2] = 0x06;
        std::copy(input.begin() + 3, input.end(), result.begin() + 3);
        return result;
    }
    // No mode switching, profile writes, persistent apply, or unknown commands.
    return std::nullopt;
}
}

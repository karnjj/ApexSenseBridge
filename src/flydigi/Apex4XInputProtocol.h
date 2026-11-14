#pragma once

#include "core/DeviceInfo.h"
#include <array>
#include <cstdint>
#include <optional>
#include <span>

namespace asb::flydigi {
// A discovery hint only. DeviceType must still be verified before any effects.
[[nodiscard]] bool isApex4XInputCandidate(const HidDeviceInfo& info);
[[nodiscard]] std::optional<std::array<std::uint8_t, 15>> translateApex4XInputCommand(
    std::span<const std::uint8_t> dinputReport);
}

#pragma once
#include "platform/HidTransport.h"
namespace asb::platform {
[[nodiscard]] HidTransport* createApex4XInputTransport(const HidDeviceInfo&, std::string&);
}

#ifdef NDEBUG
#undef NDEBUG
#endif
#include "flydigi/Apex4Protocol.h"
#include "flydigi/Apex4XInputProtocol.h"
#include "flydigi/Apex5Identity.h"
#include <array>
#include <cassert>
#include <iostream>

int main() {
    using namespace asb::flydigi;
    asb::HidDeviceInfo hint{};
    hint.vendorId=0x045E; hint.productId=0x028E; hint.usagePage=1; hint.usage=5;
    assert(!isApex4XInputCandidate(hint));
    hint.product=L"Xbox 360 Controller";
    assert(!isApex4XInputCandidate(hint));
    hint.product=L"Controller (Flydigi VADER3)";
    assert(isApex4XInputCandidate(hint));
    hint.usage=2;
    assert(!isApex4XInputCandidate(hint));

    const auto query = translateApex4XInputCommand(buildApex4IdentityRequest());
    assert(query && (*query)[0]==0xA5 && (*query)[1]==0x10);
    asb::TriggerEffect effect{};
    effect.side=asb::TriggerSide::Right; effect.mode=asb::TriggerMode::Race;
    effect.start=96; effect.p1=32; effect.matchInput=false;
    const auto trigger=translateApex4XInputCommand(buildApex4ForceTrigger(effect));
    const std::array<std::uint8_t,15> expected{0xA5,0x30,0x06,0,2,1,96,32,0,0,0,0,0,0,0};
    assert(trigger && *trigger==expected); // Bytes used in the successful RT probe.
    const auto reset=translateApex4XInputCommand(buildApex4Normal(asb::TriggerSide::Right));
    assert(reset && (*reset)[4]==2 && (*reset)[5]==0);
    assert(!translateApex4XInputCommand(buildApex4ForceTrigger(effect,true)));
    auto invalid=buildApex4ForceTrigger(effect); invalid[4]=0;
    assert(!translateApex4XInputCommand(invalid));
    assert(!translateApex4XInputCommand(std::span<const std::uint8_t>(invalid).first(14)));
    invalid[1]=0xED;
    assert(!translateApex4XInputCommand(invalid));

    std::array<std::uint8_t,32> reply{};
    reply[14]=0xA5; reply[15]=0x10; reply[16]=84;
    reply[21]=0x30; reply[22]=0x68; reply[25]=0;
    auto parsed=Apex5Identity::parseApex4XInputReply(reply);
    assert(parsed && parsed->isApex4() && parsed->firmwareVersion()==0x6830 && !parsed->isWired());
    reply[16]=28; // A real Vader must never receive APEX FORCEADAPT commands.
    parsed=Apex5Identity::parseApex4XInputReply(reply);
    assert(parsed && !parsed->supportsAdaptiveTriggers());
    reply[14]=0;
    assert(!Apex5Identity::parseApex4XInputReply(reply));
    assert(!Apex5Identity::parseApex4XInputReply(std::span<const std::uint8_t>(reply).first(31)));
    std::cout << "APEX 4 XInput codec and identity guards passed\n";
}

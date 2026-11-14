#ifdef _WIN32
#include "platform/windows/WindowsApex4XInputTransport.h"
#include "platform/windows/WindowsOverlappedIo.h"
#include "platform/XInputGamepad.h"
#include "flydigi/Apex4Protocol.h"
#include "flydigi/Apex4XInputProtocol.h"
#include "flydigi/Apex5Identity.h"
#include <hidsdi.h>
#include <xinput.h>
#include <algorithm>
#include <array>
#include <memory>

namespace asb::platform {
namespace {
using ConnectPort = HRESULT (WINAPI*)(LPCWSTR, DWORD, LPCVOID, WORD, LPSECURITY_ATTRIBUTES, HANDLE*);
using FilterReadFunction = HRESULT (WINAPI*)(HANDLE, PVOID, DWORD, LPOVERLAPPED);
constexpr DWORD kWrite = 0xC07FE004;
constexpr DWORD kCanWrite = 0xC07FE008;

class Apex4XInputTransport final : public HidTransport {
public:
    explicit Apex4XInputTransport(const HidDeviceInfo& info)
        : info_(info), io_(std::make_unique<OverlappedIo>(64)) {}
    ~Apex4XInputTransport() override {
        if (pending_ && !cancelOverlappedIo(port_, io_->overlapped)) {
            // A defective driver must never retain a pointer to freed memory.
            (void)io_.release();
        }
        if (port_ != INVALID_HANDLE_VALUE) CloseHandle(port_);
        if (filter_ != INVALID_HANDLE_VALUE) CloseHandle(filter_);
        if (physical_ != INVALID_HANDLE_VALUE) CloseHandle(physical_);
        if (library_) FreeLibrary(library_);
        if (lock_) { if (ownsLock_) ReleaseMutex(lock_); CloseHandle(lock_); }
    }
    bool open(std::string& error) {
        lock_ = CreateMutexW(nullptr, FALSE, L"Local\\ApexSenseBridgeApex4XInputFilter");
        if (!lock_) return fail(error, "Creating transport lock");
        auto acquired = WaitForSingleObject(lock_, 0);
        ownsLock_ = acquired == WAIT_OBJECT_0 || acquired == WAIT_ABANDONED;
        if (!ownsLock_) { error = "Another bridge process owns the APEX 4 XInput filter."; return false; }
        const auto slots = connectedXInputGamepads();
        if (slots.size() != 1) {
            error = "Experimental APEX 4 XInput requires exactly one connected XInput controller.";
            return false;
        }
        slot_ = slots.front();
        physical_ = CreateFileW(info_.path.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                nullptr, OPEN_EXISTING, 0, nullptr);
        if (physical_ == INVALID_HANDLE_VALUE) return fail(error, "Opening selected physical controller");
        filter_ = CreateFileW(L"\\\\.\\XboxFilter", GENERIC_READ | GENERIC_WRITE, 0,
                              nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (filter_ == INVALID_HANDLE_VALUE) return fail(error, "Opening Flydigi XboxFilter");
        library_ = LoadLibraryExW(L"fltlib.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!library_) return fail(error, "Loading Windows filter communication library");
        auto connect = reinterpret_cast<ConnectPort>(GetProcAddress(library_, "FilterConnectCommunicationPort"));
        getMessage_ = reinterpret_cast<FilterReadFunction>(GetProcAddress(library_, "FilterGetMessage"));
        if (!connect || !getMessage_) { error = "Windows filter communication functions unavailable."; return false; }
        const auto status = connect(L"\\XboxFilterPort", 0, nullptr, 0, nullptr, &port_);
        if (FAILED(status)) {
            error = "Cannot connect Flydigi filter receive port; close Space Station and run this experimental engine as administrator (HRESULT " + std::to_string(static_cast<unsigned long>(status)) + ").";
            return false;
        }
        if (!io_->event) return fail(error, "Creating filter receive event");
        return targetAvailable(error);
    }
    bool isOpen() const noexcept override { return port_ != INVALID_HANDLE_VALUE && filter_ != INVALID_HANDLE_VALUE; }
    const HidDeviceInfo& info() const noexcept override { return info_; }
    bool writeOutputReport(std::span<const std::uint8_t> input, std::string& error) override {
        if (!targetAvailable(error)) return false;
        if (input.size() == flydigi::Apex4RumbleReport{}.size() &&
            input[0] == flydigi::kApex4CommandReportId && input[1] == flydigi::kApex4CmdRumble) {
            if (!verified_) { error = "Unverified XInput target; rumble refused."; return false; }
            XINPUT_VIBRATION vibration{};
            vibration.wLeftMotorSpeed = static_cast<WORD>(input[2] * 257U);
            vibration.wRightMotorSpeed = static_cast<WORD>(input[3] * 257U);
            const auto status = XInputSetState(slot_, &vibration);
            if (status != ERROR_SUCCESS) { error = "XInput rumble failed: " + std::to_string(status); return false; }
            return true;
        }
        const auto command = flydigi::translateApex4XInputCommand(input);
        if (!command) { error = "Unsupported or persistent command refused by experimental XInput transport."; return false; }
        if ((*command)[1] != 0x10 && !verified_) { error = "Unverified XInput target; trigger write refused."; return false; }
        DWORD written = 0;
        if (!DeviceIoControl(filter_, kWrite, const_cast<std::uint8_t*>(command->data()),
                             static_cast<DWORD>(command->size()), nullptr, 0, &written, nullptr))
            return fail(error, "Writing Flydigi XInput command");
        return true;
    }
    HidReadStatus readInputReport(std::span<std::uint8_t> report,
        std::chrono::milliseconds timeout, std::size_t& bytesRead, std::string& error) override {
        bytesRead = 0;
        if (report.size() < 32) { error = "XInput receive buffer must hold 32 bytes."; return HidReadStatus::Error; }
        if (!pending_) {
            ResetEvent(io_->event);
            io_->overlapped = {};
            io_->overlapped.hEvent = io_->event;
            std::fill(io_->buffer.begin(), io_->buffer.end(), 0);
            const auto status = getMessage_(port_, io_->buffer.data(), 64, &io_->overlapped);
            pending_ = status == HRESULT_FROM_WIN32(ERROR_IO_PENDING);
            if (FAILED(status) && !pending_) { error = "Flydigi filter receive failed: " + std::to_string(static_cast<unsigned long>(status)); return HidReadStatus::Error; }
        }
        if (pending_) {
            const auto result = WaitForSingleObject(io_->event, static_cast<DWORD>((std::max)(timeout.count(), std::int64_t{0})));
            if (result == WAIT_TIMEOUT) return HidReadStatus::Timeout;
            if (result != WAIT_OBJECT_0) { fail(error, "Waiting for Flydigi filter report"); return HidReadStatus::Error; }
            DWORD received = 0;
            if (!GetOverlappedResult(port_, &io_->overlapped, &received, FALSE)) {
                pending_ = false;
                fail(error, "Completing Flydigi filter report"); return HidReadStatus::Error;
            }
            pending_ = false;
        }
        std::copy_n(io_->buffer.begin() + 32, 32, report.begin());
        bytesRead = 32;
        const auto parsed = flydigi::Apex5Identity::parseApex4XInputReply(report.first(32));
        if (parsed) verified_ = parsed->isApex4();
        return HidReadStatus::Data;
    }
private:
    static bool fail(std::string& error, const char* action) {
        const auto code = GetLastError();
        error = std::string(action) + " failed (Windows error " + std::to_string(code) + ").";
        return false;
    }
    bool targetAvailable(std::string& error) {
        const auto slots = connectedXInputGamepads();
        HIDD_ATTRIBUTES attributes{}; attributes.Size = sizeof(attributes);
        if (slots.size() != 1 || slots.front() != slot_ ||
            !HidD_GetAttributes(physical_, &attributes) ||
            attributes.VendorID != info_.vendorId || attributes.ProductID != info_.productId) {
            verified_ = false;
            error = "Selected APEX 4 XInput target disappeared or became ambiguous; write refused.";
            return false;
        }
        DWORD canWrite = 0, returned = 0;
        if (!DeviceIoControl(filter_, kCanWrite, nullptr, 0, &canWrite, sizeof(canWrite), &returned, nullptr))
            return fail(error, "Querying Flydigi filter target");
        if (!canWrite) { error = "Flydigi filter has no selected target. Reconnect the pad; selection handshake is not implemented."; return false; }
        return true;
    }
    HidDeviceInfo info_;
    HANDLE filter_ = INVALID_HANDLE_VALUE, port_ = INVALID_HANDLE_VALUE, physical_ = INVALID_HANDLE_VALUE;
    HANDLE lock_ = nullptr;
    bool ownsLock_ = false, pending_ = false, verified_ = false;
    unsigned int slot_ = 0;
    HMODULE library_ = nullptr;
    FilterReadFunction getMessage_ = nullptr;
    std::unique_ptr<OverlappedIo> io_;
};
}

HidTransport* createApex4XInputTransport(const HidDeviceInfo& info, std::string& error) {
    if (!flydigi::isApex4XInputCandidate(info)) { error = "Not a Flydigi XInput candidate."; return nullptr; }
    std::string enumerationError;
    const auto devices = enumerateHidDevices(enumerationError);
    const auto candidates = std::count_if(devices.begin(), devices.end(), flydigi::isApex4XInputCandidate);
    if (!enumerationError.empty() || candidates != 1) {
        error = "Experimental filter transport requires exactly one Flydigi XInput HID candidate.";
        return nullptr;
    }
    auto transport = std::make_unique<Apex4XInputTransport>(info);
    if (!transport->open(error)) return nullptr;
    return transport.release();
}
}
#endif

# APEX 4 XInput transport probe (experimental)

This is a transport investigation, **not working XInput bridge support**.
The upstream bridge still requires DInput for APEX 4.

The first checks on a 2.4 GHz APEX 4 showed:

- DInput/dongle idle personality: `04B4:2412`, separate vendor HID interfaces.
- XInput personality: `045E:028E`, serial label `FLYDIGI_VADER3`, no dedicated vendor HID collection.
- Flydigi's `Xbox360Filter64.sys` is installed and running.
- Opening its `\\.\XboxFilter` endpoint without elevation fails with Windows error 5.

## Run

Close games and Flydigi Space Station. Leave the pad connected in XInput mode.
Run the supplied `Run-Probe.cmd` **as administrator**. It saves `probe-result.txt`
next to the executable. No results are uploaded anywhere.

Probe version 2 prints whether its process has administrator rights. The device
open requests only generic read/write, which both observed IOCTLs require;
version 1 requested GENERIC_ALL. If version 2 reports `elevated=True` and still
gets error 5, elevation alone is insufficient and the driver access restrictions
need investigation. Do not change driver permissions to work around this.

Source build: `dotnet build -c Release` (.NET 8 SDK).
With no arguments the executable only checks filter access and write availability.
`--identify` additionally sends the read-only identity query `A5 10 42 ...`
and waits up to two seconds for its reply. It prints model, firmware and connection;
it omits device UID and raw reports.

Without `--test-rt`, the probe does not install/load/unload drivers, switch modes, perform the SDK's
special XInput rumble handshake, write profiles, or send trigger effects.
An existing selected filter target is required. `can_write=False` means that
selection/handshake still needs investigation, not that the controller is unsupported.

## Transport facts and remaining work

Flydigi's installed Windows SDK/DLL uses `\\.\XboxFilter` for output and
`\XboxFilterPort` for input. The native filter query is IOCTL `0xC07FE008`
(four-byte result); output is IOCTL `0xC07FE004` (at most 32 bytes).
The native receive buffer is 64 bytes; the 32-byte USB report begins at offset 32.
These details were inspected statically in the installed software. A subsequent
user-run probe successfully identified model 84, firmware 6.8.3.0, over the dongle
in XInput mode. A non-elevated version 2 run opened the filter using read/write
access, but receive-port connection was denied (HRESULT 0x80070005).

The older XInput wire framing is `A5 <command> ...`. Identity is command `10`
(hex), with reply marker at USB byte 14, command at 15, model at 16, firmware
at 21/22 and connection at 25. Trigger effects use `A5 30 06 <apply> ...`,
whereas upstream's DInput builder uses `05 A0 01 <apply> ...`.

Independent protocol documentation:

- https://github.com/uiltonlopes/flydigi-space-station-mac/blob/main/docs/protocol.md
- https://github.com/uiltonlopes/flydigi-space-station-mac/blob/main/docs/spacestation4-analysis.md

Next: establish controller selection without affecting unrelated pads;
validate a temporary trigger effect and reliable reset;
then integrate transport, device detection, XInput input selection, physical
controller isolation and disconnect cleanup into the C++ engine and Tray.
The `045E:028E` ID alone must never identify a device as APEX 4.

No proprietary SDK code or binaries are included in this tool.

## Version 3: optional RT test

Run `Test-RT.cmd` as administrator to identify the pad, require exactly one
XInput slot, then send a low-strength RT Race preview for two seconds. A finally
block attempts Normal on both triggers three times. No apply/save operation is
sent. This is experimental: successful output writes do not establish that the
firmware rendered or cleared the effect. Do not terminate the program during
the test. If resistance remains, power-cycle the pad. Report `trigger-result.txt`
and whether RT resistance changed and returned to normal; LT should be unchanged.

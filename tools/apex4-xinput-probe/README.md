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

Source build: `dotnet build -c Release` (.NET 8 SDK).
With no arguments the executable only checks filter access and write availability.
`--identify` additionally sends the read-only identity query `A5 10 42 ...`
and waits up to two seconds for its reply. It prints model, firmware and connection;
it omits device UID and raw reports.

The probe does not install/load/unload drivers, switch modes, perform the SDK's
special XInput rumble handshake, write profiles, or send trigger effects.
An existing selected filter target is required. `can_write=False` means that
selection/handshake still needs investigation, not that the controller is unsupported.

## Transport facts and remaining work

Flydigi's installed Windows SDK/DLL uses `\\.\XboxFilter` for output and
`\XboxFilterPort` for input. The native filter query is IOCTL `0xC07FE008`
(four-byte result); output is IOCTL `0xC07FE004` (at most 32 bytes).
The native receive buffer is 64 bytes; the 32-byte USB report begins at offset 32.
These details were inspected statically in the installed software. Successful
elevated identification has **not** yet validated them on this setup.

The older XInput wire framing is `A5 <command> ...`. Identity is command `10`
(hex), with reply marker at USB byte 14, command at 15, model at 16, firmware
at 21/22 and connection at 25. Trigger effects use `A5 30 06 <apply> ...`,
whereas upstream's DInput builder uses `05 A0 01 <apply> ...`.

Independent protocol documentation:

- https://github.com/uiltonlopes/flydigi-space-station-mac/blob/main/docs/protocol.md
- https://github.com/uiltonlopes/flydigi-space-station-mac/blob/main/docs/spacestation4-analysis.md

Next: validate elevated identification; establish controller selection without
affecting unrelated pads; validate a temporary trigger effect and reliable reset;
then integrate transport, device detection, XInput input selection, physical
controller isolation and disconnect cleanup into the C++ engine and Tray.
The `045E:028E` ID alone must never identify a device as APEX 4.

No proprietary SDK code or binaries are included in this tool.

# Experimental APEX 4 XInput transport

Status: experimental, opt-in XInput support with hardware validation on an APEX 4
(DeviceType 84, firmware 6.8.3.0) over the 2.4 GHz dongle. Gyroscope support is
outside this change. Wired USB and other firmware/units remain unvalidated.

Set `ASB_APEX4_XINPUT=1` for the experimental engine process. With no variable,
APEX 4 discovery remains DInput-only. The filter input port requires elevation.
Flydigi's already-installed/running Xbox360Filter64.sys is required; this code
does not install, start, stop, remove or redistribute that driver or its DLL.

Discovery considers the Xbox 360 VID/PID only when its HID product string names
Flydigi. It still requires a verified APEX 4 DeviceType before effects. Exactly
one connected XInput controller and one Flydigi XInput candidate are required,
because the filter selects one global target. Close Space Station before use.
If the filter reports no selected target, startup refuses: the selection
handshake is not implemented. Do not change device/driver ACLs.

The engine reuses its existing APEX 4 effect translation and write pacing. The
transport accepts only read-only identity, temporary force-trigger preview, and
standard XInput grip rumble. It rejects mode switches, profile saves and unknown
commands. Physical buttons/sticks and independent LT/RT come from XInput.
Gyroscope forwarding is not implemented in this path.

The physical controller's real HID/USB instance IDs and container are retained
for HidHide. Both its synthesized HID/`IG_` children and the verified `xusb22`
parent in the same controller container are included in isolation. Startup fails
if the parent cannot be verified. Use `--reattach-seconds 0` for initial tests; power-cycle recovery has not
been validated. Do not unplug the pad or terminate the process during a test.

## Validation recorded on 2026-11-14

- The identity probe successfully identified the controller in XInput mode.
- The owner confirmed temporary RT resistance and return to normal.
- A three-minute bridge session read buttons, sticks and simultaneous LT/RT,
  ended with exit code 0, neutralized virtual input and reported trigger reset.
- The owner confirmed the portable Tray's controller tests worked.
- After the parent-isolation fix, a fresh non-whitelisted process reported no
  visible XInput controller while the bridge was active. Applications holding
  older handles still need to be restarted after activation.
- The owner confirmed adaptive-trigger effects from https://ds.daidr.me/ through
  the virtual DualSense and bridge to the physical APEX 4. This exercises the
  full feedback path, unlike the Tray's direct controller tests.
- The owner reported working controls and vibration in Wuthering Waves. Its
  ordinary gameplay did not establish adaptive-trigger resistance.
- Rocket League vibration did not work through the virtual DualSense in the
  reported test, but worked with the bridge off using native XInput. That
  compatibility issue is unresolved; its catalogue entry requires a separate
  manual fix for adaptive triggers.

Windows CI built the engine and passed `protocol`, `identity_guard`,
`apex4_xinput` and `physical_isolation_space_station_guard`. These checks and
the observations above are not validation of every game or trigger mode.

Remaining work includes gyro forwarding, repeatable reconnect/crash recovery
tests, cold-start filter target selection, wired transport validation and
broader controller/firmware coverage. Existing DInput support remains available.

# Experimental APEX 4 XInput transport

Status: the standalone filter probe identified DeviceType 84, firmware 6.8.3.0
over the 2.4 GHz dongle. The owner confirmed a low RT Race effect was felt and
returned to normal. Full bridge/gameplay and disconnect cleanup remain unvalidated.

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
for HidHide. Its XInput `IG_` child is included in isolation, and failures remain
fatal. Use `--reattach-seconds 0` for initial tests; power-cycle recovery has not
been validated. Do not unplug the pad or terminate the process during a test.

The first complete bridge test must verify buttons, simultaneous LT/RT, adaptive
effects, rumble, no duplicate physical inputs, and return to normal after exit.
Successful codec/identity tests and an engine build do not establish gameplay.

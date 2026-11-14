# APEX 4 XInput gyro investigation

Branch: `experimental/apex4-xinput-gyro`. The validated XInput transport branch
is unchanged. Gyro decoding/forwarding is not implemented by this capture tool.

The installed Flydigi SDK labels bytes 14, 15 and 16 of ordinary legacy XInput
reports as motion. That does not establish calibrated angular rates, sign,
axis orientation, precision or availability of accelerometer data on APEX 4.
The existing DInput motion decoder has a different layout and must not be
applied to these XInput bytes without hardware evidence.

## Capture

1. Stop the bridge and close the experimental/normal Tray, games and Space Station.
2. Connect only your APEX 4 in XInput mode, using the same dongle as earlier tests.
3. Right-click `Capture-Motion.cmd` > Run as administrator.
4. After identification, press Enter and follow the timed instructions for
   stillness, yaw (level left/right turning), pitch (front edge up/down), roll
   (left/right grip up), and stick-only motion. There are still periods between
   rotations. Total recording time is approximately 48 seconds.
5. Share `motion-capture.csv` and `motion-summary.json` from the extracted folder.

Use the current profile unchanged for the first recording. The tool sends only
the read-only identity request, then passively receives filter reports. It does
not enable raw routing, change profiles/modes, hide devices or set trigger effects.

Only recognized ordinary 32-byte input reports (`00 14`, excluding vendor reply
markers) enter the CSV, sampled at most once every 5 ms. All input bytes are
retained for axis/packing analysis; they include button and stick states.
Identity/vendor replies, pipe handles and device UID are not recorded. Other
packet classes are counted by header only in the summary. Existing capture files
are not overwritten; move them aside before a repeat capture.

A zero-movement result may mean motion is disabled or unavailable in this
profile/path. It is not proof of a broken gyro. We will inspect the capture
before deciding whether another routing command or profile setup is needed.

`--self-test-motion` checks packet classification, length validation and rejection
of identity/vendor frames without opening a controller or driver.

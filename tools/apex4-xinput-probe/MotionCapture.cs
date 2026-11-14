using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// Capture facts before attempting a motion decoder. This tool never enables
// raw routing or modifies gyro/profile configuration.
static class MotionCapture
{
    public static bool IsState(ReadOnlySpan<byte> report) => report.Length == 32 &&
        report[0] == 0 && report[1] == 0x14 && report[14] is not 0xA5 and not 0x5A;

    public static int SelfTest()
    {
        byte[] sample = new byte[32];
        if (IsState(sample)) throw new Exception("Unknown packet accepted.");
        sample[1] = 0x14;
        if (!IsState(sample) || IsState(sample.AsSpan(0, 31))) throw new Exception("State length guard failed.");
        sample[14] = 0xA5; sample[15] = 0x10; sample[16] = 84;
        if (IsState(sample)) throw new Exception("Identity packet would be recorded.");
        sample[14] = 0x5A;
        if (IsState(sample)) throw new Exception("Vendor reply would be recorded.");
        byte[] profile = new byte[790]; profile[1] = 3; profile[137] = 2;
        profile[138] = 17; profile[139] = 1; profile[144] = 255;
        var parsed = ParseMotionProfile(profile, 0);
        if (parsed is not { MappingType: 2, Mapping: "RightJoystick", ActivationType: 1, ReportedSlot: 1 })
            throw new Exception("Profile motion-block offsets failed.");
        profile[137] = 0;
        if (ParseMotionProfile(profile, 0) is not { Mapping: "Off" }) throw new Exception("Off profile not recognized.");
        if (ParseMotionProfile(profile.AsSpan(0, 789), 0) is not null || ParseMotionProfile(profile, 4) is not null)
            throw new Exception("Incomplete profile or invalid slot accepted.");
        profile[1] = 4;
        if (ParseMotionProfile(profile, 0) is not null) throw new Exception("Unknown profile version accepted.");
        Console.WriteLine("Motion capture packet/identity privacy guards passed.");
        Console.WriteLine("Profile version, length, slot and motion-block guards passed.");
        return 0;
    }

    public static int Run(SafeFileHandle port, SafeFileHandle device, byte reportedSlot)
    {
        var phases = new[] {
            new Phase("still", 6, "Place the controller flat on a table. Do not touch any controls."),
            new Phase("yaw", 8, "Lift it level. Turn its front edge left and right, like a compass needle. Do not move sticks."),
            new Phase("still_after_yaw", 4, "Set it flat and keep it still."),
            new Phase("pitch", 8, "Lift it. Slowly tilt its front edge upward and downward. Do not move sticks."),
            new Phase("still_after_pitch", 4, "Set it flat and keep it still."),
            new Phase("roll", 8, "Lift it. Tilt the left grip upward, then the right grip upward. Do not move sticks."),
            new Phase("still_after_roll", 4, "Set it flat and keep it still."),
            new Phase("sticks_only", 6, "Keep it flat on the table. Move both sticks around, without rotating the controller."),
            new Phase("lt_yaw", 8, "Hold LT fully down. Keep the controller level and turn left/right without moving sticks."),
            new Phase("lt_pitch", 8, "Keep holding LT. Slowly tilt the front edge upward/downward without moving sticks."),
            new Phase("lt_roll", 8, "Keep holding LT. Tilt the left grip up, then the right grip up without moving sticks."),
            new Phase("final_still", 4, "Release LT and set the controller flat. Keep it still.")
        };
        string csvPath = Path.Combine(AppContext.BaseDirectory, "motion-capture.csv");
        string summaryPath = Path.Combine(AppContext.BaseDirectory, "motion-summary.json");
        if (File.Exists(csvPath) || File.Exists(summaryPath))
        {
            Console.Error.WriteLine("Capture files already exist. Move them to a separate folder before recording another run.");
            return 20;
        }
        using var reader = new PacketReader(port);
        var profile = ReadMotionProfile(device, reader, reportedSlot);
        Console.WriteLine("Motion profile read: " + JsonSerializer.Serialize(profile));
        if (profile is { MappingType: 0 })
        {
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(new
            {
                CaptureVersion = 2,
                Completed = false,
                ExitCode = 24,
                SettingsChanged = false,
                MotionProfile = profile,
                Reason = "The profile slot reported by identity has motion mapping Off. No recording performed."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Gyro mapping is OFF in the reported slot. Check the active slot in Space Station, enable Gyro there, apply it, then close Space Station.");
            Console.WriteLine("No profile was changed. Share motion-summary.json; do not repeat rotations with motion Off.");
            return 24;
        }
        Console.WriteLine("Guided motion capture: about 76 seconds, including rotations while holding LT.");
        Console.WriteLine("Use the profile you selected in Space Station. This tool does not change gyro settings.");
        Console.WriteLine("Follow each timed instruction. The final three rotation steps require LT held down.");
        Console.WriteLine("Ordinary input packets are saved; identity/vendor replies and pipe handles are excluded.");
        Console.WriteLine("Press Enter when ready.");
        Console.ReadLine();
        var summaries = new List<PhaseResult>();
        using var csv = new StreamWriter(new FileStream(csvPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        csv.WriteLine("elapsed_ms,phase," + string.Join(",", Enumerable.Range(0, 32).Select(i => $"b{i:D2}")));
        // Do not label reports accumulated during the user's Enter prompt as
        // current stillness. Leave any newly-armed asynchronous read alive.
        var drainClock = Stopwatch.StartNew();
        for (int n = 0; n < 20000 && drainClock.ElapsedMilliseconds < 250; n++)
            if (reader.Read(0) is null) break;
        var clock = Stopwatch.StartNew();
        bool cancelled = false;
        ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; cancelled = true; };
        Console.CancelKeyPress += cancelHandler;
        int code = 0;
        try
        {
            foreach (var phase in phases)
            {
                if (cancelled) break;
                var result = new PhaseResult(phase.Name);
                summaries.Add(result);
                Console.WriteLine($"\n{phase.Name.ToUpperInvariant()} ({phase.Seconds} seconds): {phase.Instruction}");
                var phaseStart = clock.Elapsed;
                var lastSaved = TimeSpan.MinValue;
                int lastCountdown = -1;
                while (!cancelled && clock.Elapsed - phaseStart < TimeSpan.FromSeconds(phase.Seconds))
                {
                    var left = (int)Math.Ceiling(phase.Seconds - (clock.Elapsed - phaseStart).TotalSeconds);
                    if (left != lastCountdown) { Console.WriteLine($"  {left}s remaining"); lastCountdown = left; }
                    byte[]? packet = reader.Read(50);
                    if (packet is null) { result.Timeouts++; continue; }
                    result.TotalPackets++;
                    if (!IsState(packet))
                    {
                        result.SkippedPackets++;
                        string kind = $"header={packet[0]:X2}{packet[1]:X2};vendor_reply={packet[14] is 0xA5 or 0x5A}";
                        result.RejectedKinds.TryGetValue(kind, out long count);
                        result.RejectedKinds[kind] = count + 1;
                        continue;
                    }
                    result.StatePackets++;
                    for (int i = 0; i < 32; i++)
                    {
                        result.Minimum[i] = Math.Min(result.Minimum[i], packet[i]);
                        result.Maximum[i] = Math.Max(result.Maximum[i], packet[i]);
                    }
                    var now = clock.Elapsed;
                    if (lastSaved != TimeSpan.MinValue && now - lastSaved < TimeSpan.FromMilliseconds(5)) continue;
                    csv.WriteLine(now.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + "," +
                        phase.Name + "," + string.Join(",", packet.Select(b => b.ToString(CultureInfo.InvariantCulture))));
                    lastSaved = now;
                    result.SavedPackets++;
                }
                csv.Flush();
            }
            if (cancelled) code = 21;
            else if (summaries.Any(s => s.StatePackets == 0)) code = 22;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Capture failed: " + error.Message);
            code = 23;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            foreach (var summary in summaries)
                if (summary.StatePackets == 0) Array.Fill(summary.Minimum, 0);
            File.WriteAllText(summaryPath, JsonSerializer.Serialize(new
            {
                CaptureVersion = 2,
                Completed = code == 0,
                ExitCode = code,
                SettingsChanged = false,
                ReportsContainIdentity = false,
                GyroDecodeImplemented = false,
                MotionProfile = profile,
                Phases = summaries
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"\nCapture exit code: {code}. Share motion-capture.csv and motion-summary.json.");
        Console.WriteLine("This records candidate motion bytes; it does not yet prove that raw gyro or accelerometer data is available.");
        return code;
    }

    sealed record MotionProfile(int ReportedSlot, string Version, int MappingType, string Mapping,
        int ActivationType, int ActivationKey1, int ActivationKey2);

    static MotionProfile? ReadMotionProfile(SafeFileHandle device, PacketReader reader, byte slot)
    {
        if (slot > 3) { Console.WriteLine("Unknown profile slot; skipping profile read."); return null; }
        byte[] query = new byte[15]; query[0] = 0xA5; query[1] = 0x21; query[2] = slot;
        if (!Native.DeviceIoControl(device, 0xC07FE004, query, 15, null, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            Console.WriteLine("Read-only profile request failed: " + error);
            return null;
        }
        byte[] blob = new byte[840];
        var seen = new HashSet<int>();
        int expected = 0;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            var packet = reader.Read(50);
            if (packet is null || packet[14] != 0xA5 || packet[15] != 0x22) continue;
            int index = packet[16];
            if (index == 0)
            {
                if (packet[18] != 3 || packet[17] > 1) break;
                int count = packet[17] == 0 ? 79 : 84;
                if (expected != 0 && expected != count) break;
                expected = count;
            }
            if (expected == 0 || index >= expected) continue;
            if (!seen.Add(index))
            {
                if (!packet.AsSpan(17, 10).SequenceEqual(blob.AsSpan(index * 10, 10))) break;
            }
            else packet.AsSpan(17, 10).CopyTo(blob.AsSpan(index * 10));
            if (seen.Count != expected) continue;
            return ParseMotionProfile(blob.AsSpan(0, expected * 10), slot);
        }
        Console.WriteLine("No complete supported profile snapshot arrived. Gyro enable state remains unknown.");
        return null;
    }

    static MotionProfile? ParseMotionProfile(ReadOnlySpan<byte> blob, byte slot)
    {
        if (slot > 3 || blob.Length < 2 || blob[1] != 3 || blob[0] > 1 ||
            blob.Length != (blob[0] == 0 ? 790 : 840) || blob[137] > 3) return null;
        int map = blob[137];
        return new MotionProfile(slot + 1, $"3.{blob[0]}", map,
            new[] { "Off", "LeftJoystick", "RightJoystick", "Mouse" }[map],
            blob[139], blob[138], blob[144]);
    }

    sealed record Phase(string Name, int Seconds, string Instruction);
    sealed class PhaseResult(string name)
    {
        public string Phase { get; } = name;
        public long TotalPackets { get; set; }
        public long StatePackets { get; set; }
        public long SavedPackets { get; set; }
        public long SkippedPackets { get; set; }
        public long Timeouts { get; set; }
        public Dictionary<string, long> RejectedKinds { get; } = new();
        public int[] Minimum { get; } = Enumerable.Repeat(255, 32).ToArray();
        public int[] Maximum { get; } = new int[32];
    }

    sealed class PacketReader(SafeFileHandle port) : IDisposable
    {
        readonly IntPtr buffer = Marshal.AllocHGlobal(64);
        readonly IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Overlapped>());
        readonly EventWaitHandle ready = new(false, EventResetMode.ManualReset);
        bool pending;
        public byte[]? Read(int timeoutMs)
        {
            if (!pending)
            {
                ready.Reset();
                Marshal.Copy(new byte[64], 0, buffer, 64);
                Marshal.StructureToPtr(new Native.Overlapped { Event = ready.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                int hr = Native.FilterGetMessage(port, buffer, 64, overlapped);
                pending = hr == unchecked((int)0x800703E5);
                if (hr < 0 && !pending) throw new Exception($"FilterGetMessage HRESULT 0x{hr:X8}");
            }
            if (pending)
            {
                if (!ready.WaitOne(timeoutMs)) return null;
                bool completed = Native.GetOverlappedResult(port, overlapped, out _, false);
                int error = Marshal.GetLastWin32Error();
                if (!completed) throw new Win32Exception(error);
                pending = false;
            }
            byte[] packet = new byte[32];
            Marshal.Copy(buffer + 32, packet, 0, 32);
            return packet;
        }
        public void Dispose()
        {
            if (pending)
            {
                Native.CancelIoEx(port, overlapped);
                if (!ready.WaitOne(500))
                {
                    // Retain memory while a defective driver still owns it.
                    Console.Error.WriteLine("Filter cancellation did not finish in 500 ms. Retaining the small receive buffer until process exit.");
                    return;
                }
                Native.GetOverlappedResult(port, overlapped, out _, false);
            }
            Marshal.FreeHGlobal(overlapped);
            Marshal.FreeHGlobal(buffer);
            ready.Dispose();
        }
    }
}

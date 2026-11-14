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
        Console.WriteLine("Motion capture packet/identity privacy guards passed.");
        return 0;
    }

    public static int Run(SafeFileHandle port)
    {
        var phases = new[] {
            new Phase("still", 6, "Place the controller flat on a table. Do not touch any controls."),
            new Phase("yaw", 8, "Lift it level. Turn its front edge left and right, like a compass needle. Do not move sticks."),
            new Phase("still_after_yaw", 4, "Set it flat and keep it still."),
            new Phase("pitch", 8, "Lift it. Slowly tilt its front edge upward and downward. Do not move sticks."),
            new Phase("still_after_pitch", 4, "Set it flat and keep it still."),
            new Phase("roll", 8, "Lift it. Tilt the left grip upward, then the right grip upward. Do not move sticks."),
            new Phase("still_after_roll", 4, "Set it flat and keep it still."),
            new Phase("sticks_only", 6, "Keep it flat on the table. Move both sticks around, without rotating the controller.")
        };
        string csvPath = Path.Combine(AppContext.BaseDirectory, "motion-capture.csv");
        string summaryPath = Path.Combine(AppContext.BaseDirectory, "motion-summary.json");
        if (File.Exists(csvPath) || File.Exists(summaryPath))
        {
            Console.Error.WriteLine("Capture files already exist. Move them to a separate folder before recording another run.");
            return 20;
        }
        Console.WriteLine("Guided motion capture: about 48 seconds. No buttons/triggers are needed.");
        Console.WriteLine("Use your current profile for the first run. No gyro settings will be changed.");
        Console.WriteLine("Each motion step is followed by a still step. Follow the messages as they appear.");
        Console.WriteLine("Ordinary input packets are saved; identity/vendor replies and pipe handles are excluded.");
        Console.WriteLine("Press Enter when ready.");
        Console.ReadLine();
        var summaries = new List<PhaseResult>();
        using var csv = new StreamWriter(new FileStream(csvPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        csv.WriteLine("elapsed_ms,phase," + string.Join(",", Enumerable.Range(0, 32).Select(i => $"b{i:D2}")));
        using var reader = new PacketReader(port);
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
                CaptureVersion = 1,
                Completed = code == 0,
                ExitCode = code,
                SettingsChanged = false,
                ReportsContainIdentity = false,
                GyroDecodeImplemented = false,
                Phases = summaries
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"\nCapture exit code: {code}. Share motion-capture.csv and motion-summary.json.");
        Console.WriteLine("This records candidate motion bytes; it does not yet prove that raw gyro or accelerometer data is available.");
        return code;
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

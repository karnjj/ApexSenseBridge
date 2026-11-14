using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

// Experimental transport probe. Uses an already-running Flydigi filter only;
// never installs, starts, stops or removes a driver, or changes saved profiles.
if (!OperatingSystem.IsWindows()) return 1;
if (args.Any(a => a is not "--identify" and not "--test-rt"))
{
    Console.Error.WriteLine("Usage: Apex4XInputProbe [--identify] [--test-rt]");
    return 1;
}
using var identity = WindowsIdentity.GetCurrent();
bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine("probe_version=3 elevated=" + elevated + " system=" + identity.IsSystem);
// Both observed IOCTLs require read/write access. GENERIC_ALL additionally
// requests unrelated rights (such as WRITE_DAC) that the device ACL may deny.
using var device = Native.CreateFileW(@"\\.\XboxFilter", 0xC0000000, 0, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
int openError = Marshal.GetLastWin32Error();
if (device.IsInvalid)
{
    Console.Error.WriteLine($"Cannot open existing XboxFilter ({openError}): " + new Win32Exception(openError).Message);
    if (openError == 5)
        Console.Error.WriteLine(elevated
            ? "The elevated process was denied access. Driver access restrictions need further investigation."
            : "This process is not elevated. Run Run-Probe.cmd as administrator, then check elevated=True in this log.");
    return 2;
}
byte[] writable = new byte[4];
if (!Native.DeviceIoControl(device, 0xC07FE008, null, 0, writable, 4, out _, IntPtr.Zero))
{
    Console.Error.WriteLine("Filter query failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    return 3;
}
Console.WriteLine("filter_open=true can_write=" + (BitConverter.ToUInt32(writable) != 0));
if (!args.Contains("--identify") && !args.Contains("--test-rt")) return 0;
if (BitConverter.ToUInt32(writable) == 0)
{
    Console.Error.WriteLine("The filter has no selected controller. No command sent.");
    return 4;
}
int hr = Native.FilterConnectCommunicationPort(@"\XboxFilterPort", 0, IntPtr.Zero, 0, IntPtr.Zero, out var port);
using (port)
{
    if (hr < 0)
    {
        Console.Error.WriteLine($"Cannot connect filter receive port: HRESULT 0x{hr:X8}");
        return 5;
    }
    // Native DLL receives 64 bytes: 24-byte envelope, 8-byte pipe handle,
    // then the 32-byte USB report. Keep the buffer alive until cancellation ends.
    IntPtr buffer = Marshal.AllocHGlobal(64);
    IntPtr overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Overlapped>());
    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset);
    bool pending = false;
    try
    {
        bool querySent = false;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        int unrelated = 0;
        while (DateTime.UtcNow < deadline)
        {
            ready.Reset();
            Marshal.StructureToPtr(new Native.Overlapped { Event = ready.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
            hr = Native.FilterGetMessage(port, buffer, 64, overlapped);
            pending = hr == unchecked((int)0x800703E5);
            if (hr < 0 && !pending)
            {
                Console.Error.WriteLine($"Receive failed: HRESULT 0x{hr:X8}");
                return 6;
            }
            if (!querySent)
            {
                byte[] query = new byte[15]; query[0] = 0xA5; query[1] = 0x10; query[2] = 0x42;
                if (!Native.DeviceIoControl(device, 0xC07FE004, query, (uint)query.Length, null, 0, out _, IntPtr.Zero))
                {
                    Console.Error.WriteLine("Identity write failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                    return 7;
                }
                querySent = true;
            }
            if (pending && !ready.WaitOne(TimeSpan.FromMilliseconds(Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds))))
            {
                Console.Error.WriteLine("Identity receive timed out. No trigger/profile commands sent.");
                return 8;
            }
            if (pending && !Native.GetOverlappedResult(port, overlapped, out _, false))
            {
                Console.Error.WriteLine("Identity receive failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
                return 9;
            }
            pending = false;
            byte[] report = new byte[32]; Marshal.Copy(buffer + 32, report, 0, 32);
            // Do not print UID or other unique device identifiers.
            if ((report[14] is not 0xA5 and not 0x5A) || report[15] != 0x10)
            {
                unrelated++;
                continue;
            }
            Console.WriteLine($"device_type={report[16]} firmware={report[22] >> 4}.{report[22] & 15}.{report[21] >> 4}.{report[21] & 15} connection={(report[25] == 1 ? "USB" : "dongle")}");
            bool apex4 = report[16] is 84 or 86 or 87 or 92 or 93 or 102 or 103 or 104;
            Console.WriteLine("apex4_identity=" + apex4);
            if (apex4 && args.Contains("--test-rt"))
            {
                // The filter selects a global target. Refuse ambiguous setups.
                int slots = 0;
                for (uint slot = 0; slot < 4; slot++)
                    if (Native.XInputGetState(slot, new byte[32]) == 0) slots++;
                if (slots != 1)
                {
                    Console.Error.WriteLine("Trigger test requires exactly one connected XInput controller. No trigger command sent.");
                    return 12;
                }
                // Older XInput framing: force-trigger subcommand 06, preview
                // (apply=0), RT=2, Race=1, start=96, resistance=32.
                byte[] effect = new byte[15];
                effect[0] = 0xA5; effect[1] = 0x30; effect[2] = 0x06;
                effect[4] = 2; effect[5] = 1; effect[6] = 96; effect[7] = 32;
                byte[] reset = new byte[15];
                reset[0] = 0xA5; reset[1] = 0x30; reset[2] = 0x06; reset[4] = 3;
                bool testWritten = false, resetWritten = false;
                try
                {
                    testWritten = Native.DeviceIoControl(device, 0xC07FE004, effect, 15, null, 0, out _, IntPtr.Zero);
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"trigger_write={testWritten} error={(testWritten ? 0 : error)}");
                    if (testWritten) Thread.Sleep(2000);
                }
                finally
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        resetWritten |= Native.DeviceIoControl(device, 0xC07FE004, reset, 15, null, 0, out _, IntPtr.Zero);
                        Thread.Sleep(25);
                    }
                    Console.WriteLine("reset_write=" + resetWritten);
                    Console.WriteLine("Write success confirms transport acceptance only. Report what RT felt like and whether it returned to normal.");
                }
                return testWritten && resetWritten ? 0 : 13;
            }
            return apex4 ? 0 : 11;
        }
        Console.Error.WriteLine($"No identity reply; received {unrelated} other reports. No trigger/profile commands sent.");
        return 10;
    }
    finally
    {
        if (pending)
        {
            Native.CancelIoEx(port, overlapped);
            Native.GetOverlappedResult(port, overlapped, out _, true);
        }
        Marshal.FreeHGlobal(overlapped); Marshal.FreeHGlobal(buffer);
    }
}

static class Native
{
    [DllImport("xinput1_4.dll")] public static extern uint XInputGetState(uint index, [Out] byte[] state);
    [StructLayout(LayoutKind.Sequential)] public struct Overlapped { public UIntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)] public static extern int FilterConnectCommunicationPort(string name, uint options, IntPtr context, ushort contextSize, IntPtr security, out SafeFileHandle port);
    [DllImport("fltlib.dll")] public static extern int FilterGetMessage(SafeFileHandle port, IntPtr message, uint size, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}

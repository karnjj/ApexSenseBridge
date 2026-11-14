using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Experimental transport probe. Uses an already-running Flydigi filter only;
// never installs, starts, stops or removes a driver, or changes saved profiles.
if (!OperatingSystem.IsWindows()) return 1;
if (args.Any(a => a is not "--identify"))
{
    Console.Error.WriteLine("Usage: Apex4XInputProbe [--identify]");
    return 1;
}
using var device = Native.CreateFileW(@"\\.\XboxFilter", 0x10000000, 0, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
int openError = Marshal.GetLastWin32Error();
if (device.IsInvalid)
{
    Console.Error.WriteLine($"Cannot open existing XboxFilter ({openError}): " + new Win32Exception(openError).Message);
    return 2;
}
byte[] writable = new byte[4];
if (!Native.DeviceIoControl(device, 0xC07FE008, null, 0, writable, 4, out _, IntPtr.Zero))
{
    Console.Error.WriteLine("Filter query failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    return 3;
}
Console.WriteLine("filter_open=true can_write=" + (BitConverter.ToUInt32(writable) != 0));
if (!args.Contains("--identify")) return 0;
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
    [StructLayout(LayoutKind.Sequential)] public struct Overlapped { public UIntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);
    [DllImport("fltlib.dll", CharSet = CharSet.Unicode)] public static extern int FilterConnectCommunicationPort(string name, uint options, IntPtr context, ushort contextSize, IntPtr security, out SafeFileHandle port);
    [DllImport("fltlib.dll")] public static extern int FilterGetMessage(SafeFileHandle port, IntPtr message, uint size, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}

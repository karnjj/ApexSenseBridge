$ErrorActionPreference = 'Stop'
if (-not (Get-Process -Name ApexSenseBridge -ErrorAction SilentlyContinue)) {
    Write-Host 'Start the experimental Tray bridge first, then run this check while it is active.'
    Read-Host 'Press Enter to close'
    exit 2
}
# This fresh PowerShell process is not on the bridge's HidHide whitelist.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class XInputVisibility {
    [DllImport("xinput1_4.dll", EntryPoint="XInputGetState")]
    static extern uint State(uint slot, IntPtr data);
    public static uint[] Visible() {
        var slots = new List<uint>();
        var buffer = Marshal.AllocHGlobal(32);
        try { for (uint slot=0;slot<4;slot++) if (State(slot,buffer)==0) slots.Add(slot); }
        finally { Marshal.FreeHGlobal(buffer); }
        return slots.ToArray();
    }
}
'@
$slots = @([XInputVisibility]::Visible())
$result = if ($slots.Count -eq 0) {
    'PASS: No XInput controller is visible to this fresh process while the bridge is active.'
} else {
    'FAIL: XInput remains visible on slot(s): ' + ($slots -join ', ')
}
$result | Tee-Object -FilePath (Join-Path $PSScriptRoot 'hiding-result.txt')
Write-Host 'This check assumes only your APEX 4 is connected. It does not test already-open browser/Steam handles.'
Read-Host 'Press Enter to close'

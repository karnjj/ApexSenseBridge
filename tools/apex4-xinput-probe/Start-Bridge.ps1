$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Right-click Start-Bridge.cmd and select Run as administrator.'
    Read-Host 'Press Enter to close'
    exit 2
}
$env:ASB_APEX4_XINPUT = '1'
$engine = Join-Path $PSScriptRoot 'ApexSenseBridge.exe'
$log = Join-Path $PSScriptRoot 'bridge-result.txt'
if (-not (Test-Path -LiteralPath $engine)) { throw 'Experimental engine missing from this folder.' }
Write-Host 'APEX 4 XInput bridge: experimental three-minute game test.'
Write-Host 'Close Space Station and the normal ApexSenseBridge Tray. Connect only your APEX 4.'
Write-Host 'Keep the controller still during startup. Wait for Bridge running before opening the game.'
Write-Host 'Disable Steam Input for the selected native DualSense game.'
Write-Host 'The test stops after three minutes. Let it finish and restore the controller.'
Read-Host 'Press Enter to start'
# The installed drivers and libVIIPER are reused; the installed app is untouched.
$savedErrorPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue' # Native stderr warnings must not abort cleanup.
& $engine bridge-triggers --seconds 180 --reattach-seconds 0 --virtual-backend integrated --rumble --trigger-strength 50 --vibration-strength 50 2>&1 |
    ForEach-Object { $_.ToString() } |
    Tee-Object -FilePath $log
$engineExit = $LASTEXITCODE
$ErrorActionPreference = $savedErrorPreference
"Exit code: $engineExit" | Tee-Object -FilePath $log -Append
Write-Host 'Share bridge-result.txt and what worked in the game.'
Read-Host 'Press Enter to close'
exit $engineExit

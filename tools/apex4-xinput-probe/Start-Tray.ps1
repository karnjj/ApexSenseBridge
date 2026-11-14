$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Right-click Start-Tray.cmd and choose Run as administrator.'
    Read-Host 'Press Enter to close'
    exit 2
}
if (Get-Process -Name ApexSenseBridgeTray,ApexSenseBridge -ErrorAction SilentlyContinue) {
    Write-Host 'Close the existing ApexSenseBridge Tray and let any bridge test finish first.'
    Read-Host 'Press Enter to close'
    exit 3
}
$env:ASB_APEX4_XINPUT = '1'
$tray = Join-Path $PSScriptRoot 'ApexSenseBridgeTray.exe'
$engine = Join-Path $PSScriptRoot 'ApexSenseBridge.exe'
if (-not (Test-Path -LiteralPath $tray) -or -not (Test-Path -LiteralPath $engine)) {
    throw 'Keep the Tray and experimental engine together in the extracted folder.'
}
# InstallLocator prioritizes the engine beside the Tray. Child processes inherit
# the opt-in variable and administrator rights; no installed files are replaced.
Start-Process -FilePath $tray -ArgumentList '--show' -WorkingDirectory $PSScriptRoot -WindowStyle Normal

# Stream Machine for Windows: installer. Safe to re-run (it updates). Undo with uninstall.ps1.
#
#   One line (PowerShell):  irm https://github.com/JMS1717/stream-machine/releases/latest/download/install.ps1 | iex
#   Or: download StreamMachine-windows-x64.zip from Releases, unzip, double-click Install.cmd
#   From a source checkout:  .\install.ps1          (builds first; needs the .NET 8 SDK)
#
# It asks for administrator rights once (Windows' UAC prompt), then:
#   - installs StreamMachine.Service.exe, StreamMachine.Agent.exe and streammachine.exe to C:\Program Files\StreamMachine
#     (and puts streammachine on the PATH)
#   - registers the "StreamMachine" service (LocalSystem, automatic, 10 s preshutdown so the TV turns off at shutdown)
#   - registers the "Stream Machine Agent" task: the controller overlay, at your sign-in, as you (not elevated)
#   - Game Bar: "Allow your controller to open Game Bar" off, so the Xbox button goes to the stream (Game Bar stays)
#   - your LG TV's HDMI audio becomes the default playback device, if one is connected
#   - the network adapter you're online with: Wake on Magic Packet on, Energy-Efficient Ethernet / Green Ethernet off
#     (so the TV can wake this PC); power plan "USB selective suspend" off while plugged in
#   - then runs the setup wizard: find your TVs, pair them, pick your streaming client
# Everything it changes is recorded first (C:\ProgramData\StreamMachine\backup) and put back by uninstall.ps1.
[CmdletBinding()]
param([switch]$SkipBuild, [switch]$NoSetup, [string]$User = $env:USERNAME)
$ErrorActionPreference = 'Stop'
$Repo = 'JMS1717/stream-machine'

# ------------------------------------------------------------------ run from a download (irm | iex)
$here = if ($PSScriptRoot) { $PSScriptRoot } else { '' }
if (-not $here -or (-not (Test-Path (Join-Path $here 'bin')) -and -not (Test-Path (Join-Path $here 'src')))) {
    Write-Host 'Downloading the latest Stream Machine release…' -ForegroundColor Cyan
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("stream-machine-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory $tmp | Out-Null
    $zip = Join-Path $tmp 'sm.zip'
    Invoke-WebRequest "https://github.com/$Repo/releases/latest/download/StreamMachine-windows-x64.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip $tmp -Force
    $pass = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $tmp 'install.ps1'))
    if ($NoSetup) { $pass += '-NoSetup' }
    & powershell.exe @pass
    return
}

$Dest = Join-Path $env:ProgramFiles 'StreamMachine'
$Data = Join-Path $env:ProgramData 'StreamMachine'
$Backup = Join-Path $Data 'backup\before-install.json'
$TaskName = 'Stream Machine Agent'
$UsbSuspend = '2a737441-1930-4402-8d77-b2bebba308a3', '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
$Exes = 'StreamMachine.Service.exe', 'StreamMachine.Agent.exe', 'streammachine.exe'

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function IsAdmin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }

# ------------------------------------------------------------------ build (source checkout, as the user)
$bin = Join-Path $here 'bin'
if (-not (Test-Path (Join-Path $bin 'StreamMachine.Service.exe')) -and -not $SkipBuild) {
    Step 'Build (self-contained, single file, x64)'
    $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (-not (Test-Path $dotnet)) { throw '.NET 8 SDK missing (winget install --id Microsoft.DotNet.SDK.8 -e), or use a release download' }
    foreach ($p in 'StreamMachine.Service', 'StreamMachine.Agent', 'StreamMachine.Cli') {
        & $dotnet publish (Join-Path $here "src\$p") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -o $bin --nologo -v quiet
        if ($LASTEXITCODE) { throw "build of $p failed" }
    }
}

# ------------------------------------------------------------------ elevate once
if (-not (IsAdmin)) {
    $pass = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-SkipBuild', '-User', $User)
    if ($NoSetup) { $pass += '-NoSetup' }
    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $pass -Wait -PassThru
    exit $p.ExitCode
}

New-Item -ItemType Directory -Force (Join-Path $Data 'logs'), (Join-Path $Data 'keys'), (Join-Path $Data 'backup') | Out-Null
Start-Transcript -Path (Join-Path $Data ("logs\install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))) | Out-Null
$Sid = (New-Object Security.Principal.NTAccount($env:COMPUTERNAME, $User)).Translate([Security.Principal.SecurityIdentifier]).Value
$UserHive = "Registry::HKEY_USERS\$Sid"
# The adapter this PC is online with (the default route), for Wake-on-LAN.
$Nic = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric |
    ForEach-Object { Get-NetAdapter -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue } | Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } |
    Select-Object -First 1 -ExpandProperty Name

try {
# ------------------------------------------------------------------ record the state before the first install
Step 'Record current settings (for uninstall)'
if (-not (Test-Path $Backup)) {
    $gb = Get-ItemProperty "$UserHive\Software\Microsoft\GameBar" -Name UseNexusForGameBarEnabled -ErrorAction SilentlyContinue
    $nicProps = @{}
    if ($Nic) {
        foreach ($k in '*WakeOnMagicPacket', '*EEE', 'EnableGreenEthernet', 'AdvancedEEE', 'EnableExtraPowerSaving') {
            $v = Get-NetAdapterAdvancedProperty -Name $Nic -RegistryKeyword $k -ErrorAction SilentlyContinue
            if ($v) { $nicProps[$k] = [string]($v.RegistryValue | Select-Object -First 1) }
        }
    }
    $usb = ((powercfg /qh SCHEME_CURRENT @UsbSuspend | Select-String 'Current AC') -replace '.*0x', '')
    $audio = @(& (Join-Path $bin 'StreamMachine.Agent.exe') audio-list | Where-Object { $_ -match 'default:\d' })
    [ordered]@{
        date = (Get-Date).ToString('s'); user = $User; sid = $Sid; nic = $Nic; nicProps = $nicProps
        gameBarUseNexus = if ($gb) { $gb.UseNexusForGameBarEnabled } else { $null }
        usbSelectiveSuspendAc = $usb; audioDefaults = $audio
    } | ConvertTo-Json -Depth 5 | Set-Content $Backup -Encoding UTF8
    "  saved $Backup"
} else { "  kept the record from the first install" }

# ------------------------------------------------------------------ stop the running copy
Step 'Stop the running copy (if any)'
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Get-Process StreamMachine.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service StreamMachine -ErrorAction SilentlyContinue) {
    $svcPid = (Get-CimInstance Win32_Service -Filter "Name='StreamMachine'").ProcessId
    Stop-Service StreamMachine -Force -ErrorAction SilentlyContinue
    (Get-Service StreamMachine).WaitForStatus('Stopped', '00:00:15')
    # "Stopped" is reported before the process has exited and released its exe.
    if ($svcPid) { Wait-Process -Id $svcPid -Timeout 20 -ErrorAction SilentlyContinue }
}
Get-Process StreamMachine.Agent -ErrorAction SilentlyContinue | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue

# ------------------------------------------------------------------ files
Step "Install to $Dest"
New-Item -ItemType Directory -Force $Dest | Out-Null
foreach ($e in $Exes) {
    $src = Join-Path $bin $e
    if (-not (Test-Path $src)) { throw "missing $src" }
    for ($try = 1; ; $try++) {
        try { Copy-Item $src $Dest -Force; break }
        catch { if ($try -ge 10) { throw }; Start-Sleep -Milliseconds 700 }
    }
}
Get-ChildItem $Dest -Filter *.exe | ForEach-Object { '  {0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
$path = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (($path -split ';') -notcontains $Dest) { [Environment]::SetEnvironmentVariable('Path', "$path;$Dest", 'Machine'); "  added $Dest to PATH" }

Step "Data folder $Data"
$cfg = Join-Path $Data 'config.json'
if (Test-Path $cfg) {
    $c = Get-Content $cfg -Raw | ConvertFrom-Json
    $c | Add-Member -NotePropertyName AllowedUserSid -NotePropertyValue $Sid -Force
    $c | ConvertTo-Json -Depth 6 | Set-Content $cfg -Encoding UTF8
    '  kept your TVs and settings'
} else {
    [ordered]@{ Tvs = @(); AllowedUserSid = $Sid } | ConvertTo-Json | Set-Content $cfg -Encoding UTF8
    '  new config (TVs are added by the setup wizard)'
}
# SYSTEM and Administrators only (pairing keys live here); you may read the logs.
icacls $Data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
icacls (Join-Path $Data 'logs') /grant "*${Sid}:(OI)(CI)RX" | Out-Null

# ------------------------------------------------------------------ service
Step 'Service StreamMachine'
$svcBin = '"' + (Join-Path $Dest 'StreamMachine.Service.exe') + '"'
if (-not (Get-Service StreamMachine -ErrorAction SilentlyContinue)) {
    New-Service -Name StreamMachine -BinaryPathName $svcBin -DisplayName 'Stream Machine' -StartupType Automatic `
        -Description 'Turns your TVs on at boot and wake, off at sleep and shutdown (not restart). Control pipe: \\.\pipe\StreamMachine.' | Out-Null
} else { sc.exe config StreamMachine binPath= $svcBin start= auto | Out-Null }
sc.exe failure StreamMachine reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
# If a Fast Startup shutdown ever stopped the service, the next boot still starts it when the network comes up.
sc.exe triggerinfo StreamMachine start/networkon | Out-Null
# How long Windows waits for the preshutdown handler (TVs off) before continuing a shutdown.
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\StreamMachine' -Name PreshutdownTimeout -Value 10000 -Type DWord
Start-Service StreamMachine
(Get-Service StreamMachine).WaitForStatus('Running', '00:00:15')
'  running'

# ------------------------------------------------------------------ agent task
Step "Scheduled task '$TaskName'"
$action = New-ScheduledTaskAction -Execute (Join-Path $Dest 'StreamMachine.Agent.exe')
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:COMPUTERNAME\$User"
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -StartWhenAvailable -Hidden
$principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$User" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description 'Stream Machine: controller overlay (hold View + Menu or the Xbox button), wake flow, home screen.' -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
'  registered and started'

# ------------------------------------------------------------------ Windows settings
Step 'Game Bar: the controller''s Xbox button no longer opens it'
New-Item "$UserHive\Software\Microsoft\GameBar" -Force -ErrorAction SilentlyContinue | Out-Null
Set-ItemProperty "$UserHive\Software\Microsoft\GameBar" -Name UseNexusForGameBarEnabled -Value 0 -Type DWord

Step 'Default audio: the TV'
& (Join-Path $Dest 'StreamMachine.Agent.exe') audio-default | ForEach-Object { "  $_" }

Step "Network adapter: $(if ($Nic) { $Nic } else { 'none found' })"
if ($Nic) {
    $set = @{ '*WakeOnMagicPacket' = '1'; '*EEE' = '0'; 'EnableGreenEthernet' = '0'; 'AdvancedEEE' = '0'; 'EnableExtraPowerSaving' = '0' }
    $restartNic = $false
    foreach ($k in $set.Keys) {
        $cur = Get-NetAdapterAdvancedProperty -Name $Nic -RegistryKeyword $k -ErrorAction SilentlyContinue
        if (-not $cur) { continue }
        if ([string]($cur.RegistryValue | Select-Object -First 1) -ne $set[$k]) {
            Set-NetAdapterAdvancedProperty -Name $Nic -RegistryKeyword $k -RegistryValue $set[$k] -NoRestart
            "  $k -> $($set[$k])"; $restartNic = $true
        }
    }
    if ($restartNic) { '  applying (the network drops for a few seconds)'; Restart-NetAdapter -Name $Nic; Start-Sleep -Seconds 8 }
    else { '  already set' }
}
powercfg /setacvalueindex SCHEME_CURRENT @UsbSuspend 0; powercfg /setactive SCHEME_CURRENT
'  USB selective suspend (plugged in): off'

Step 'Check'
Start-Sleep -Seconds 2
& (Join-Path $Dest 'streammachine.exe') status | Select-Object -First 3 | ForEach-Object { "  $_" }
}
catch {
    # Never leave the PC without its TV automation: bring back whatever is installed.
    Write-Warning "install failed: $($_.Exception.Message); restarting the installed service and agent"
    if (Get-Service StreamMachine -ErrorAction SilentlyContinue) { Start-Service StreamMachine -ErrorAction SilentlyContinue }
    Start-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    throw
}
finally { Stop-Transcript | Out-Null }

if (-not $NoSetup -and [Environment]::UserInteractive) {
    Step 'Setup: TVs and streaming client'
    & (Join-Path $Dest 'streammachine.exe') setup
}
Write-Host "`nDone. Hold View + Menu (or the Xbox button) on your controller to open Stream Machine." -ForegroundColor Green

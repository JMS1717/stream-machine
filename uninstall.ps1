# Stream Machine for Windows: removes it and puts back what install.ps1 changed
# (from C:\ProgramData\StreamMachine\backup\before-install.json).
#   .\uninstall.ps1          remove; keep your TV pairings and logs in C:\ProgramData\StreamMachine
#   .\uninstall.ps1 -Purge   also delete C:\ProgramData\StreamMachine (TV pairings, logs, backup)
[CmdletBinding()]
param([switch]$Purge)
$ErrorActionPreference = 'Continue'
$Dest = Join-Path $env:ProgramFiles 'StreamMachine'
$Data = Join-Path $env:ProgramData 'StreamMachine'
$BackupFile = Join-Path $Data 'backup\before-install.json'
$TaskName = 'Stream Machine Agent'
$UsbSuspend = '2a737441-1930-4402-8d77-b2bebba308a3', '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'

function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $pass = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath); if ($Purge) { $pass += '-Purge' }
    exit (Start-Process powershell.exe -Verb RunAs -ArgumentList $pass -Wait -PassThru).ExitCode
}
$b = if (Test-Path $BackupFile) { Get-Content $BackupFile -Raw | ConvertFrom-Json } else { Write-Warning "no $BackupFile; removing files only"; $null }

if ($b) {
    # Before the files go: the agent puts the old default audio devices back.
    Step 'Default audio devices'
    $agent = Join-Path $Dest 'StreamMachine.Agent.exe'
    foreach ($line in @($b.audioDefaults)) {
        if ($line -match '(\{0\.0\.0\.00000000\}\.\{[0-9a-fA-F-]+\})\s+default:([0-9,]+)') { & $agent audio-set-id $Matches[1] $Matches[2] | ForEach-Object { "  $_" } }
    }
}

Step 'Agent and service'
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Get-Process StreamMachine.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service StreamMachine -ErrorAction SilentlyContinue) {
    $svcPid = (Get-CimInstance Win32_Service -Filter "Name='StreamMachine'").ProcessId
    Stop-Service StreamMachine -Force
    if ($svcPid) { Wait-Process -Id $svcPid -Timeout 20 -ErrorAction SilentlyContinue }
    sc.exe delete StreamMachine | Out-Null
    '  removed'
}

if ($b) {
    $hive = "Registry::HKEY_USERS\$($b.sid)"
    Step 'Game Bar'
    if ($null -eq $b.gameBarUseNexus) { Remove-ItemProperty "$hive\Software\Microsoft\GameBar" -Name UseNexusForGameBarEnabled -ErrorAction SilentlyContinue; '  back to the Windows default' }
    else { Set-ItemProperty "$hive\Software\Microsoft\GameBar" -Name UseNexusForGameBarEnabled -Value $b.gameBarUseNexus -Type DWord; "  restored" }

    # Version 1.0 recorded the adapter's properties under "nic"; 2.0 records the adapter name there.
    if ($b.nic -and $b.nic -isnot [string]) {
        $props = $b.nic
        $name = Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | ForEach-Object { Get-NetAdapter -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue } | Where-Object Status -eq 'Up' | Select-Object -First 1 -ExpandProperty Name
        $b.nic = $name
        $b | Add-Member -NotePropertyName nicProps -NotePropertyValue $props -Force
    }
    if ($b.nic) {
        Step "Network adapter $($b.nic)"
        $changed = $false
        foreach ($p in $b.nicProps.PSObject.Properties) {
            $cur = Get-NetAdapterAdvancedProperty -Name $b.nic -RegistryKeyword $p.Name -ErrorAction SilentlyContinue
            if ($cur -and [string]($cur.RegistryValue | Select-Object -First 1) -ne $p.Value) {
                Set-NetAdapterAdvancedProperty -Name $b.nic -RegistryKeyword $p.Name -RegistryValue $p.Value -NoRestart; "  $($p.Name) -> $($p.Value)"; $changed = $true
            }
        }
        if ($changed) { Restart-NetAdapter -Name $b.nic }
    }
    if ($b.usbSelectiveSuspendAc) { powercfg /setacvalueindex SCHEME_CURRENT @UsbSuspend ([Convert]::ToInt32($b.usbSelectiveSuspendAc, 16)); powercfg /setactive SCHEME_CURRENT; '  USB selective suspend restored' }
}

Step 'Files'
$path = [Environment]::GetEnvironmentVariable('Path', 'Machine')
[Environment]::SetEnvironmentVariable('Path', (($path -split ';') | Where-Object { $_ -and $_ -ne $Dest }) -join ';', 'Machine')
Remove-Item $Dest -Recurse -Force -ErrorAction SilentlyContinue
"  removed $Dest"
if ($Purge) { Remove-Item $Data -Recurse -Force -ErrorAction SilentlyContinue; "  removed $Data" }
else { "  kept $Data (TV pairings, logs; -Purge removes it)" }
'Stream Machine is uninstalled.'

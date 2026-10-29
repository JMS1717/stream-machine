# Removes the Stream Machine TV hook (everything install-tv-hook.ps1 put on the TV). SSH key auth only.
[CmdletBinding()]
param([Parameter(Mandatory, HelpMessage = 'The TV IP address (streammachine tv list shows it)')][string]$TvIp)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tv-hook\common.ps1')
$ip = $TvIp
$key = Find-SshKey
if (-not $key -or -not (Test-TvSsh $ip $key)) { Show-SshOwnerSteps $ip $key; exit 2 }
Invoke-TvSsh $ip $key @'
[ -f /tmp/streammachine-wake.pid ] && kill $(cat /tmp/streammachine-wake.pid) 2>/dev/null
pkill -f 'getPowerState.*subscribe' 2>/dev/null
rm -f /var/lib/webosbrew/init.d/50-streammachine-wake /tmp/streammachine-wake.pid
rm -rf /home/root/streammachine
echo removed
'@

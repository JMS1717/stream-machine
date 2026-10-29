# Installs the TV-side hook: when the TV turns on, it sends Wake-on-LAN to this PC (TV remote on -> PC wakes -> stream).
# SSH key authentication only; this script never asks for, sends or stores a password.
#   .\install-tv-hook.ps1            install (or update) and start the watcher on the TV
#   .\uninstall-tv-hook.ps1          remove it again
# On the TV it writes only: /home/root/streammachine/ (script, wol.js, pc-mac) and
# /var/lib/webosbrew/init.d/50-streammachine-wake (Homebrew Channel's boot hook folder). webOS itself is not modified.
[CmdletBinding()]
param([Parameter(Mandatory, HelpMessage = 'The TV IP address (streammachine tv list shows it)')][string]$TvIp, [string]$Nic = '')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'tv-hook\common.ps1')

$ip = $TvIp
$key = Find-SshKey
if (-not $key -or -not (Test-TvSsh $ip $key)) { Show-SshOwnerSteps $ip $key; exit 2 }

if (-not $Nic) {
    # The adapter this PC is online with (the default route): the one the TV's magic packet must reach.
    $Nic = Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric |
        ForEach-Object { Get-NetAdapter -InterfaceIndex $_.ifIndex -ErrorAction SilentlyContinue } |
        Where-Object { $_.Status -eq 'Up' -and -not $_.Virtual } | Select-Object -First 1 -ExpandProperty Name
}
$mac = (Get-NetAdapter -Name $Nic).MacAddress -replace '-', ':'
"TV at $ip, PC $Nic MAC $mac"
$stage = Join-Path ([IO.Path]::GetTempPath()) "sm-tvhook-$PID"
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($f in 'streammachine-wake.sh', 'wol.js', '50-streammachine-wake') {
    # LF line endings regardless of git settings; the TV's shell needs them.
    $text = (Get-Content (Join-Path $PSScriptRoot "tv-hook\$f") -Raw) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $stage $f), $text)
}
[IO.File]::WriteAllText((Join-Path $stage 'pc-mac'), "$mac`n")
try {
    Invoke-TvSsh $ip $key 'mkdir -p /home/root/streammachine /var/lib/webosbrew/init.d'
    & scp -q -i $key @SshOpts (Join-Path $stage 'streammachine-wake.sh') (Join-Path $stage 'wol.js') (Join-Path $stage 'pc-mac') "root@${ip}:/home/root/streammachine/"
    if ($LASTEXITCODE) { throw 'scp failed' }
    & scp -q -i $key @SshOpts (Join-Path $stage '50-streammachine-wake') "root@${ip}:/var/lib/webosbrew/init.d/"
    if ($LASTEXITCODE) { throw 'scp failed' }
    Invoke-TvSsh $ip $key @'
chmod 755 /home/root/streammachine/streammachine-wake.sh /var/lib/webosbrew/init.d/50-streammachine-wake
[ -f /tmp/streammachine-wake.pid ] && kill $(cat /tmp/streammachine-wake.pid) 2>/dev/null
pkill -f 'getPowerState.*subscribe' 2>/dev/null
/var/lib/webosbrew/init.d/50-streammachine-wake
sleep 1; echo "watcher pid: $(cat /tmp/streammachine-wake.pid 2>/dev/null)"; command -v node || echo "no node; will use ether-wake"
'@
    'Installed. Test: turn the TV off with its remote, sleep the PC, turn the TV on with the remote -> the PC wakes.'
    'TV-side log: ssh root@TV cat /tmp/streammachine-wake.log'
}
finally { Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue }

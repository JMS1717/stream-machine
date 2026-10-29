#!/bin/sh
# Stream Machine TV hook: when the TV turns on (remote, or boot), send Wake-on-LAN to the living-room PC.
# Installed by install-tv-hook.ps1 into /home/root/streammachine; started at TV boot by
# /var/lib/webosbrew/init.d/50-streammachine-wake. User space only; remove with uninstall-tv-hook.ps1.
DIR="$(cd "$(dirname "$0")" && pwd)"
PC_MAC="$(cat "$DIR/pc-mac" 2>/dev/null)"
LOG=/tmp/streammachine-wake.log
[ -n "$PC_MAC" ] || { echo "$(date) no pc-mac file" >> "$LOG"; exit 1; }
echo $$ > /tmp/streammachine-wake.pid

wake() {
    echo "$(date '+%F %T') TV on -> Wake-on-LAN to $PC_MAC" >> "$LOG"
    if command -v node >/dev/null 2>&1; then node "$DIR/wol.js" "$PC_MAC" >> "$LOG" 2>&1 && return; fi
    if command -v ether-wake >/dev/null 2>&1; then ether-wake -b "$PC_MAC" >> "$LOG" 2>&1 && return; fi
    busybox ether-wake -b "$PC_MAC" >> "$LOG" 2>&1
}

last=""
while true; do
    # Subscribe to the TV's power state; one JSON line per change ("Active", "Active Standby", "Suspend", ...).
    luna-send -i 'luna://com.webos.service.tvpower/power/getPowerState' '{"subscribe":true}' 2>>"$LOG" |
    while read -r line; do
        case "$line" in
            *'"state":"Active"'*|*'"state": "Active"'*) state=Active ;;
            *'"state"'*) state=other ;;
            *) continue ;;
        esac
        if [ "$state" = Active ] && [ "$last" != Active ]; then wake; fi
        last=$state
    done
    echo "$(date '+%F %T') power-state subscription ended; resubscribing" >> "$LOG"
    sleep 5
done

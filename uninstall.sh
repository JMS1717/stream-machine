#!/usr/bin/env bash
# Stream Machine for Linux: removes the service, the agent, the command and (with --purge) the TV pairings.
set -euo pipefail
PURGE=0; [ "${1:-}" = "--purge" ] && PURGE=1
if [ "$(id -u)" -ne 0 ]; then exec sudo bash "$0" "$@"; fi
SM_USER="${SUDO_USER:-}"

systemctl disable --now stream-machine.service 2>/dev/null || true
rm -f /etc/systemd/system/stream-machine.service
systemctl daemon-reload
if [ -n "$SM_USER" ]; then
    USER_HOME=$(getent passwd "$SM_USER" | cut -d: -f6)
    runuser -u "$SM_USER" -- env XDG_RUNTIME_DIR="/run/user/$(id -u "$SM_USER")" systemctl --user disable --now stream-machine-agent.service 2>/dev/null || true
    rm -f "$USER_HOME/.config/systemd/user/stream-machine-agent.service"
fi
rm -f /usr/local/bin/streammachine
rm -rf /usr/local/lib/stream-machine
if [ "$PURGE" = 1 ]; then
    rm -rf /var/lib/stream-machine
    groupdel stream-machine 2>/dev/null || true
    echo "Removed, including TV pairings."
else
    echo "Removed. TV pairings kept in /var/lib/stream-machine (--purge removes them)."
fi

#!/usr/bin/env bash
# Stream Machine for Linux: installer. Safe to re-run (it updates). Undo with uninstall.sh.
#
#   One line:   curl -fsSL https://github.com/JMS1717/stream-machine/releases/latest/download/install.sh | bash
#   Or: download stream-machine-linux-x64.tar.gz from Releases, extract, run ./install.sh
#
# Works on systemd distros, including immutable ones (Bazzite, Fedora Atomic, SteamOS-like): everything goes to
# /usr/local/lib/stream-machine, /etc/systemd and /var/lib/stream-machine, which stay writable there.
#   - stream-machine.service (root): turns your TVs on at boot and wake, off at sleep and shutdown (not reboot)
#   - stream-machine-agent.service (your user): starts the stream after wake, leaves Big Picture when a stream ends
#   - streammachine command (/usr/local/bin): setup, status, TVs, volume, settings
#   - group "stream-machine" (you're added to it) may talk to the service's socket
set -euo pipefail
REPO="JMS1717/stream-machine"
PREFIX=/usr/local/lib/stream-machine
UNIT=/etc/systemd/system/stream-machine.service
GROUP=stream-machine

say() { printf '\n\033[1;36m== %s\033[0m\n' "$*"; }
die() { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(uname -m)" = "x86_64" ] || die "only x86_64 is supported for now"
command -v systemctl >/dev/null || die "systemd is required"

# Run as root, remembering who the desktop user is.
if [ "$(id -u)" -ne 0 ]; then
    if [ -f "${BASH_SOURCE[0]:-}" ]; then exec sudo bash "${BASH_SOURCE[0]}" "$@"; fi
    # Piped from curl: fetch the script again under sudo.
    exec sudo bash -c "$(curl -fsSL "https://github.com/$REPO/releases/latest/download/install.sh")"
fi
SM_USER="${SUDO_USER:-}"
[ -n "$SM_USER" ] && [ "$SM_USER" != "root" ] || die "run this as your normal user (it uses sudo), not as root"
USER_HOME=$(getent passwd "$SM_USER" | cut -d: -f6)
USER_UID=$(id -u "$SM_USER")

# Files: next to this script (extracted release), or download the latest release.
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-.}")" 2>/dev/null && pwd || echo .)"
if [ ! -f "$HERE/bin/stream-machine-service" ]; then
    say "Downloading the latest Stream Machine release"
    WORK=$(mktemp -d)
    curl -fsSL "https://github.com/$REPO/releases/latest/download/stream-machine-linux-x64.tar.gz" | tar -xz -C "$WORK"
    HERE="$WORK"
fi

say "Install to $PREFIX"
systemctl stop stream-machine.service 2>/dev/null || true
mkdir -p "$PREFIX"
install -m 0755 "$HERE/bin/stream-machine-service" "$PREFIX/stream-machine-service"
install -m 0755 "$HERE/bin/streammachine" "$PREFIX/streammachine"
mkdir -p /usr/local/bin
ln -sf "$PREFIX/streammachine" /usr/local/bin/streammachine
# SELinux (Fedora, Bazzite): give the binaries the labels a system service may run.
command -v restorecon >/dev/null && restorecon -R "$PREFIX" /usr/local/bin/streammachine 2>/dev/null || true
echo "  $(du -sh "$PREFIX" | cut -f1) installed"

say "Group $GROUP"
getent group "$GROUP" >/dev/null || groupadd --system "$GROUP"
if ! id -nG "$SM_USER" | tr ' ' '\n' | grep -qx "$GROUP"; then
    usermod -aG "$GROUP" "$SM_USER"
    echo "  added $SM_USER (takes effect at your next login)"
fi

say "Service stream-machine.service"
cat > "$UNIT" <<EOF
[Unit]
Description=Stream Machine: TVs follow this PC (on at boot/wake, off at sleep/shutdown)
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
ExecStart=$PREFIX/stream-machine-service
Restart=always
RestartSec=5
RuntimeDirectory=stream-machine
RuntimeDirectoryMode=0755
StateDirectory=stream-machine
StateDirectoryMode=0700
# Shutdown: the service holds a systemd "delay" lock and turns the TVs off when logind announces the shutdown.
TimeoutStopSec=15

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
systemctl enable --now stream-machine.service
sleep 1
systemctl is-active --quiet stream-machine.service && echo "  running" || { journalctl -u stream-machine.service -n 20 --no-pager; die "the service didn't start"; }

say "Agent for $SM_USER (starts the stream after wake)"
AGENT_DIR="$USER_HOME/.config/systemd/user"
install -d -o "$SM_USER" -g "$(id -gn "$SM_USER")" "$AGENT_DIR"
cat > "$AGENT_DIR/stream-machine-agent.service" <<EOF
[Unit]
Description=Stream Machine agent: starts the stream after wake, leaves Big Picture when a stream ends
After=graphical-session.target

[Service]
ExecStart=/usr/local/bin/streammachine agent
Restart=always
RestartSec=10

[Install]
WantedBy=default.target
EOF
chown "$SM_USER" "$AGENT_DIR/stream-machine-agent.service"
runuser -u "$SM_USER" -- env XDG_RUNTIME_DIR="/run/user/$USER_UID" systemctl --user daemon-reload 2>/dev/null || true
runuser -u "$SM_USER" -- env XDG_RUNTIME_DIR="/run/user/$USER_UID" systemctl --user enable --now stream-machine-agent.service 2>/dev/null \
    && echo "  enabled" || echo "  enable it after your next login: systemctl --user enable --now stream-machine-agent"

say "Setup: TVs and streaming client"
if [ -t 0 ] || [ -e /dev/tty ]; then
    # The new group isn't active in this login yet; sg gives it to the setup run.
    runuser -u "$SM_USER" -- sg "$GROUP" -c "/usr/local/bin/streammachine setup" < /dev/tty || true
else
    echo "  run: streammachine setup"
fi

printf '\n\033[1;32mDone.\033[0m Your TVs now follow this PC. Status: streammachine status\n'
printf 'Log out and back in once so "streammachine" can reach the service without sudo.\n'

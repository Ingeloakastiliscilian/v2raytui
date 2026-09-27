#!/usr/bin/env bash
# Installs v2rayn-tui from an unpacked release archive.
#
#   ./install.sh                      binary → ~/.local/bin, systemd user unit → ~/.config/systemd/user
#   ./install.sh --system             binary → /usr/local/bin, unit → /etc/systemd/user (uses sudo)
#   ./install.sh --service            also start the daemon now and at every login
#   ./install.sh --data DIR           data directory for the daemon (default: ~/.local/share/v2rayN)
#   ./install.sh --uninstall [--system]
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
system=0 service=0 uninstall=0 data=""
while [ $# -gt 0 ]; do
    case "$1" in
        --system) system=1 ;;
        --service) service=1 ;;
        --uninstall) uninstall=1 ;;
        --data) shift; data="$1" ;;
        -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
    shift
done

if [ "$system" = 1 ]; then
    bindir=/usr/local/bin unitdir=/etc/systemd/user sudo=sudo
else
    bindir="$HOME/.local/bin" unitdir="$HOME/.config/systemd/user" sudo=""
fi

if [ "$uninstall" = 1 ]; then
    systemctl --user disable --now v2rayn-tui 2>/dev/null || true
    $sudo rm -f "$bindir/v2rayn-tui" "$unitdir/v2rayn-tui.service"
    systemctl --user daemon-reload 2>/dev/null || true
    echo "Removed. Your data (~/.local/share/v2rayN or --data dir) is kept."
    exit 0
fi

$sudo install -Dm755 "$here/v2rayn-tui" "$bindir/v2rayn-tui"

exec_start="$bindir/v2rayn-tui${data:+ --data $data} daemon"
tmp="$(mktemp)"
sed "s#^ExecStart=.*#ExecStart=$exec_start#" "$here/v2rayn-tui.service" > "$tmp"
$sudo install -Dm644 "$tmp" "$unitdir/v2rayn-tui.service"
rm -f "$tmp"
systemctl --user daemon-reload 2>/dev/null || true

echo "Installed: $bindir/v2rayn-tui ($("$bindir/v2rayn-tui" --version))"
case ":$PATH:" in
    *":$bindir:"*) ;;
    *) echo "Note: $bindir is not in PATH — add it to your shell profile." ;;
esac

if [ "$service" = 1 ]; then
    systemctl --user enable --now v2rayn-tui
    echo "Daemon started and enabled at login (systemctl --user status v2rayn-tui)."
    echo "To keep it running without an open session: loginctl enable-linger $USER"
else
    echo "Run the TUI: v2rayn-tui    Background daemon at login: systemctl --user enable --now v2rayn-tui"
fi

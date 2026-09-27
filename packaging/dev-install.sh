#!/usr/bin/env bash
# Fast local install into ~/.local/bin: one self-contained publish for the current
# architecture, no tarball/.deb step. For release artifacts use build.sh instead.
#
#   packaging/dev-install.sh              build + install
#   packaging/dev-install.sh --restart    also stop the running background instance
#                                         and reopen the interface (new binary takes over)
set -euo pipefail
cd "$(dirname "$0")/.."

case "$(uname -m)" in
    x86_64) rid=linux-x64 ;;
    aarch64|arm64) rid=linux-arm64 ;;
    *) echo "unsupported arch: $(uname -m)" >&2; exit 1 ;;
esac

bindir="$HOME/.local/bin"
target="$bindir/v2rayn-tui"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "==> publishing ($rid)…"
dotnet publish src/V2RayTui/V2RayTui.csproj -c Release -r "$rid" --self-contained true -o "$tmp" -v quiet -nologo

mkdir -p "$bindir"
install -m755 "$tmp/v2rayn-tui" "$target"
version="$("$target" --version)"
echo "==> installed: $target ($version)"

case ":$PATH:" in
    *":$bindir:"*) ;;
    *) echo "Note: $bindir is not in PATH." ;;
esac

if [ "${1:-}" = "--restart" ]; then
    "$target" stop || true
    exec "$target"
fi

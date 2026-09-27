#!/usr/bin/env bash
# Builds release artifacts into dist/:
#   v2rayn-tui-<ver>-<rid>.tar.gz   self-contained binary + install.sh + systemd unit + docs
#   v2rayn-tui_<ver>_<arch>.deb     Debian/Ubuntu package (Linux RIDs)
#   SHA256SUMS
# Usage: packaging/build.sh [rid...]        (default: linux-x64 linux-arm64)
#        VERSION=1.2.3 packaging/build.sh   (default: <Version> from the csproj)
set -euo pipefail
cd "$(dirname "$0")/.."

PROJECT=src/V2RayTui/V2RayTui.csproj
VERSION=${VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT")}
RIDS=("$@")
[ ${#RIDS[@]} -eq 0 ] && RIDS=(linux-x64 linux-arm64)
DIST=dist
MAINTAINER="$(git config user.name 2>/dev/null || echo v2rayn-tui) <$(git config user.email 2>/dev/null || echo root@localhost)>"

[ -f external/v2rayN/v2rayN/ServiceLib/ServiceLib.csproj ] || git submodule update --init --recursive external/v2rayN

rm -rf "$DIST/build" "$DIST/stage" "$DIST/deb"
mkdir -p "$DIST"

for rid in "${RIDS[@]}"; do
    echo "==> $rid"
    pub="$DIST/build/$rid"
    dotnet publish "$PROJECT" -c Release -r "$rid" --self-contained true -p:TuiVersion="$VERSION" -o "$pub" -v quiet -nologo
    exe=v2rayn-tui
    [[ "$rid" == win-* ]] && exe=v2rayn-tui.exe

    # --- tarball ---------------------------------------------------------
    name="v2rayn-tui-$VERSION-$rid"
    stage="$DIST/stage/$name"
    mkdir -p "$stage"
    cp "$pub/$exe" "$stage/"
    cp README.md LICENSE contrib/v2rayn-tui.service packaging/install.sh "$stage/"
    chmod 755 "$stage/$exe" "$stage/install.sh"
    tar -C "$DIST/stage" -czf "$DIST/$name.tar.gz" "$name"

    # --- .deb --------------------------------------------------------------
    case "$rid" in
        linux-x64) arch=amd64 ;;
        linux-arm64) arch=arm64 ;;
        *) continue ;;
    esac
    root="$DIST/deb/$arch"
    install -Dm755 "$pub/$exe" "$root/usr/bin/v2rayn-tui"
    install -Dm644 contrib/v2rayn-tui.service "$root/usr/lib/systemd/user/v2rayn-tui.service"
    sed -i 's#^ExecStart=.*#ExecStart=/usr/bin/v2rayn-tui daemon#; /^# Install:/,/^# start it again/d' "$root/usr/lib/systemd/user/v2rayn-tui.service"
    install -Dm644 README.md "$root/usr/share/doc/v2rayn-tui/README.md"
    install -Dm644 LICENSE "$root/usr/share/doc/v2rayn-tui/copyright"
    mkdir -p "$root/DEBIAN"
    size=$(du -sk "$root/usr" | cut -f1)
    cat > "$root/DEBIAN/control" <<EOF
Package: v2rayn-tui
Version: $VERSION
Section: net
Priority: optional
Architecture: $arch
Installed-Size: $size
Maintainer: $MAINTAINER
Depends: libc6 (>= 2.27), libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl1.1, ca-certificates, libfontconfig1, libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66
Recommends: sudo
Suggests: xclip | wl-clipboard
Homepage: https://github.com/2dust/v2rayN
Description: terminal UI for the v2rayN engine (xray / sing-box)
 Subscriptions, share links, routing, system proxy and TUN of v2rayN in a
 terminal, plus parallel server testing, background re-testing and the
 "Alive" group of working fast servers. Includes a headless daemon mode
 (systemd user unit: systemctl --user enable --now v2rayn-tui).
 Cores and geo files are downloaded on first run.
EOF
    cat > "$root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
echo "v2rayn-tui installed. Run: v2rayn-tui   (daemon: systemctl --user enable --now v2rayn-tui)"
EOF
    chmod 755 "$root/DEBIAN/postinst"
    fakeroot dpkg-deb --build -Zxz "$root" "$DIST/v2rayn-tui_${VERSION}_${arch}.deb" >/dev/null
done

rm -rf "$DIST/build" "$DIST/stage" "$DIST/deb"
(cd "$DIST" && sha256sum ./*.tar.gz ./*.deb 2>/dev/null | sed 's# \./# #' > SHA256SUMS)
echo "==> $DIST:"
ls -lh "$DIST"

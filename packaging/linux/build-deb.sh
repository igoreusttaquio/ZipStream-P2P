#!/usr/bin/env bash
# Gera o pacote .deb (Debian/Ubuntu/Mint) a partir do dotnet publish linux-x64 self-contained.
# Uso: build-deb.sh <pasta_publish> <versao> <pasta_saida>
# Instala em /opt/zipstream, com atalho no menu de aplicativos e o comando "zipstream".
set -euo pipefail

publish_dir=$1
version=$2
out_dir=$3
here=$(cd "$(dirname "$0")" && pwd)

root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
chmod 755 "$root"

install -d "$root/DEBIAN" "$root/opt/zipstream" "$root/usr/bin" \
    "$root/usr/share/applications" "$root/usr/share/icons/hicolor/256x256/apps"
cp -a "$publish_dir/." "$root/opt/zipstream/"
find "$root/opt/zipstream" -type d -exec chmod 755 {} +
find "$root/opt/zipstream" -type f -exec chmod 644 {} +
chmod 755 "$root/opt/zipstream/ZipP2P.App"
ln -s /opt/zipstream/ZipP2P.App "$root/usr/bin/zipstream"
install -m 644 "$here/zipstream.desktop" "$root/usr/share/applications/zipstream.desktop"
install -m 644 "$here/zipstream.png" "$root/usr/share/icons/hicolor/256x256/apps/zipstream.png"

# O runtime .NET vem junto; ICU/OpenSSL e as bibliotecas X11/fontconfig do Avalonia vêm do sistema.
cat > "$root/DEBIAN/control" <<EOF
Package: zipstream
Version: $version
Section: net
Priority: optional
Architecture: amd64
Maintainer: Igor Silva <igoreusttaquio@gmail.com>
Installed-Size: $(du -sk "$root/opt" | cut -f1)
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libssl3 | libssl3t64 | libssl1.1, libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67 | libicu66, libfontconfig1, libx11-6, libice6, libsm6
Homepage: https://github.com/igoreusttaquio/ZipStream-P2P
Description: Compress and send files peer-to-peer with end-to-end encryption
 ZipStream compresses files and folders and sends them directly between
 two computers (WebRTC), encrypted end to end with AES-256-GCM.
EOF

mkdir -p "$out_dir"
dpkg-deb --build --root-owner-group "$root" "$out_dir/zipstream_${version}_amd64.deb"

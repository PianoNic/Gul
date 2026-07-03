#!/bin/sh
set -e

repo="PianoNic/Gul"
bindir="${GUL_INSTALL_DIR:-$HOME/.local/bin}"

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Linux) os_rid="linux" ;;
  Darwin) os_rid="osx" ;;
  *) echo "gul: unsupported OS '$os'" >&2; exit 1 ;;
esac

case "$arch" in
  x86_64|amd64) arch_rid="x64" ;;
  aarch64|arm64) arch_rid="arm64" ;;
  *) echo "gul: unsupported architecture '$arch'" >&2; exit 1 ;;
esac

asset="gul-${os_rid}-${arch_rid}"
url="https://github.com/${repo}/releases/latest/download/${asset}"

echo "Installing gul (${os_rid}-${arch_rid}) from the latest release..."
mkdir -p "$bindir"
tmp="$(mktemp)"
if command -v curl >/dev/null 2>&1; then
  curl -fSL "$url" -o "$tmp"
else
  wget -O "$tmp" "$url"
fi
chmod +x "$tmp"
mv "$tmp" "$bindir/gul"

echo "Installed to $bindir/gul"
case ":$PATH:" in
  *":$bindir:"*) ;;
  *) echo "Add it to your PATH:  export PATH=\"$bindir:\$PATH\"" ;;
esac
echo "Next: gul remote https://gul.example.com"

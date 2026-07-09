#!/usr/bin/env sh
# Install the latest `shonkor` release binary (Linux/macOS) — no .NET SDK required.
# Usage:  curl -fsSL https://raw.githubusercontent.com/MrNedimBoztepe/Shonkor/main/scripts/install.sh | sh
set -e

REPO="MrNedimBoztepe/Shonkor"
os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Linux)  plat="linux" ;;
  Darwin) plat="osx" ;;
  *) echo "Unsupported OS: $os (use the .NET tool: dotnet tool install -g Shonkor)"; exit 1 ;;
esac

case "$arch" in
  x86_64|amd64) a="x64" ;;
  arm64|aarch64) a="arm64" ;;
  *) echo "Unsupported architecture: $arch"; exit 1 ;;
esac

# Linux currently ships x64 only.
if [ "$plat" = "linux" ] && [ "$a" != "x64" ]; then a="x64"; fi

asset="shonkor-${plat}-${a}"
url="https://github.com/${REPO}/releases/latest/download/${asset}"
dest="${HOME}/.local/bin"
mkdir -p "$dest"

echo "Downloading ${asset} ..."
curl -fsSL "$url" -o "${dest}/shonkor"
chmod +x "${dest}/shonkor"
echo "Installed: ${dest}/shonkor"

case ":$PATH:" in
  *":${dest}:"*) ;;
  *) echo "NOTE: add ${dest} to PATH, e.g.:  echo 'export PATH=\"${dest}:\$PATH\"' >> ~/.profile" ;;
esac

echo "Next: shonkor mcp install   (register the MCP server in your agent clients)"

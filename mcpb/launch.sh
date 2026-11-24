#!/bin/sh
# Claude Desktop names one command per OS, not per CPU, so the right macOS binary is chosen here.
dir="$(cd "$(dirname "$0")" && pwd)"
case "$(uname -m)" in
  arm64) rid=osx-arm64 ;;
  *) rid=osx-x64 ;;
esac
bin="$dir/$rid/PnPPowerShell.MCPServer"
# A zip does not reliably keep the executable bit.
chmod +x "$bin" 2>/dev/null
exec "$bin" "$@"

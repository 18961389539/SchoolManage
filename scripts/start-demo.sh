#!/usr/bin/env bash
# Start the customer demo for Cloud Agent Apps. Listens on 0.0.0.0:$PORT.
# The database is created on first start and filled with demonstration accounts.
# Windows service installation is unchanged; do not set SCHOOL_DEMO for a school server.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

# Cloud Agent Apps provides Node. Install the .NET 10 SDK for this user when the image
# does not already have it, then keep using `dotnet run` exactly as the demo contract says.
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet_major=""
if command -v dotnet >/dev/null 2>&1; then
  dotnet_major="$(dotnet --version 2>/dev/null | cut -d. -f1 || true)"
fi
if [[ "$dotnet_major" != "10" ]]; then
  if ! command -v curl >/dev/null 2>&1; then
    echo "需要 .NET 10 SDK。" >&2
    exit 1
  fi
  dotnet_home="${DOTNET_ROOT:-$HOME/.dotnet}"
  echo "正在安装 .NET 10 SDK 到 ${dotnet_home}" >&2
  installer="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --channel 10.0 --install-dir "$dotnet_home"
  rm -f "$installer"
  export DOTNET_ROOT="$dotnet_home"
  export PATH="${dotnet_home}:${PATH}"
  hash -r
fi
if ! command -v dotnet >/dev/null 2>&1 || [[ "$(dotnet --version | cut -d. -f1)" != "10" ]]; then
  echo "需要 .NET 10 SDK。" >&2
  exit 1
fi
if ! command -v npm >/dev/null 2>&1; then
  echo "需要 Node.js 22.5+ 和 npm，用来构建管理界面。" >&2
  exit 1
fi

export SCHOOL_DEMO=1
export PORT="${PORT:-5080}"
export SCHOOL_DATA_DIR="${SCHOOL_DATA_DIR:-$root/.local/demo-data}"
mkdir -p "$SCHOOL_DATA_DIR"

npm --prefix web install
npm --prefix web run build
mkdir -p src/School.Training/wwwroot
cp -a web/dist/. src/School.Training/wwwroot/

exec dotnet run --project src/School.Training --no-launch-profile

#!/usr/bin/env bash
# Start the customer demo for Cloud Agent Apps. Listens on 0.0.0.0:$PORT.
# The database is created on first start and filled with demonstration accounts.
# Windows service installation is unchanged; do not set SCHOOL_DEMO for a school server.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "需要 .NET 10 SDK。" >&2
  exit 1
fi
if ! command -v npm >/dev/null 2>&1; then
  echo "需要 Node.js 22.5+ 和 npm，用来构建管理界面。" >&2
  exit 1
fi

export SCHOOL_DEMO=1
export PORT="${PORT:-5080}"
if [ -z "${SCHOOL_DEMO_PASSWORD:-}" ]; then
  echo "需要环境变量 SCHOOL_DEMO_PASSWORD。不要把口令写进仓库、页面或接口。" >&2
  exit 1
fi
export SCHOOL_DATA_DIR="${SCHOOL_DATA_DIR:-$root/.local/demo-data}"
mkdir -p "$SCHOOL_DATA_DIR"

npm --prefix web install
npm --prefix web run build
mkdir -p src/School.Training/wwwroot
cp -a web/dist/. src/School.Training/wwwroot/

exec dotnet run --project src/School.Training --no-launch-profile

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
export SCHOOL_DATA_DIR="${SCHOOL_DATA_DIR:-$root/.local/demo-data}"
mkdir -p "$SCHOOL_DATA_DIR"

# The shared demonstration password stays in this process and in a gitignored file.
# It is not written into source, the login page, or /api/status.
generate_demo_password() {
  python3 -c 'import secrets,string; letters=string.ascii_letters; digits=string.digits; alphabet=letters+digits; chars=[secrets.choice(letters), secrets.choice(digits)]+[secrets.choice(alphabet) for _ in range(22)]; secrets.SystemRandom().shuffle(chars); print("".join(chars), end="")'
}
demo_password="${SCHOOL_DEMO_PASSWORD:-}"
if [ "${#demo_password}" -lt 12 ]; then
  demo_password="$(generate_demo_password)"
fi
export SCHOOL_DEMO_PASSWORD="$demo_password"
unset demo_password

npm --prefix web install
npm --prefix web run build
mkdir -p src/School.Training/wwwroot
cp -a web/dist/. src/School.Training/wwwroot/

exec dotnet run --project src/School.Training --no-launch-profile

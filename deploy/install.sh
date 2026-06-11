#!/usr/bin/env bash
#
# Build, publish, and install the kgsm-monitor daemon. Run as root.
#
#   sudo ./deploy/install.sh            # publish + install binary + install unit (does NOT enable)
#   sudo ./deploy/install.sh --enable   # the above, then `systemctl enable --now`
#
# Installing the unit alone changes nothing about a running system; the daemon only
# starts when you enable it (explicitly, here or later). Idempotent: re-running
# republishes and replaces the binary/unit in place.
#
set -euo pipefail

PREFIX="/opt/kgsm-monitor"
UNIT_DST="/etc/systemd/system/kgsm-monitor.service"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_DIR/src/Monitor/Monitor.csproj"
UNIT_SRC="$REPO_DIR/src/Monitor/deploy/kgsm-monitor.service"
PUBLISH_DIR="$REPO_DIR/artifacts/publish"
RID="${RID:-linux-x64}"

ENABLE=0
[[ "${1:-}" == "--enable" ]] && ENABLE=1

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
    echo "error: must run as root (writes ${PREFIX} and ${UNIT_DST})" >&2
    exit 1
fi

echo ">> Publishing Native AOT binary (${RID})..."
dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH_DIR"

echo ">> Installing binary to ${PREFIX}..."
install -d "$PREFIX"
install -m 0755 "$PUBLISH_DIR/kgsm-monitor" "$PREFIX/kgsm-monitor"

echo ">> Installing systemd unit to ${UNIT_DST}..."
install -m 0644 "$UNIT_SRC" "$UNIT_DST"
systemctl daemon-reload

if [[ "$ENABLE" -eq 1 ]]; then
    echo ">> Enabling and starting kgsm-monitor..."
    systemctl enable --now kgsm-monitor
    systemctl --no-pager status kgsm-monitor || true
else
    cat <<EOF

Installed. The daemon is NOT running yet. To start it:

    systemctl enable --now kgsm-monitor

Then verify:

    curl --unix-socket /run/kgsm-monitor/metrics.sock http://localhost/metrics | jq

EOF
fi

#!/usr/bin/env bash
#
# Build + deploy the kgsm-monitor daemon in one go.
#
#   ./deploy/deploy.sh
#
# Publishes the Native-AOT binary as YOU (the invoking, service-owning user) and uses sudo ONLY
# for the steps that touch systemd / root-owned paths. The artifact is a single self-contained
# native binary — it needs NO .NET runtime on the host.
#
#   * the binary is installed to /opt/kgsm-monitor (stale files pruned),
#   * the systemd unit is (re)installed with User=/Group= rewritten to the invoking user, only
#     when it changed,
#   * deploy is verified by an actual "ok" from GET /health over the metrics unix socket.
#
# The monitor has no required env file (its unit's Environment= lines set sane defaults; the
# optional /etc/kgsm-monitor/kgsm-monitor.env overrides them).
#
# Non-interactive: SUDO='sudo -A' SUDO_ASKPASS=/path/to/askpass ./deploy/deploy.sh
#
set -euo pipefail

# ── Paths / config ────────────────────────────────────────────────────────────
PREFIX="/opt/kgsm-monitor"
UNIT_DST="/etc/systemd/system/kgsm-monitor.service"
SERVICE="kgsm-monitor"

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_DIR/src/Monitor/Monitor.csproj"
UNIT_SRC="$REPO_DIR/src/Monitor/deploy/kgsm-monitor.service"
PUBLISH_DIR="$REPO_DIR/artifacts/publish"
RID="${RID:-linux-x64}"
SVC_USER="${KGSM_MONITOR_USER:-$(id -un)}"
SVC_GROUP="${KGSM_MONITOR_GROUP:-$(id -gn)}"
SUDO="${SUDO:-sudo}"

# Health is HTTP-over-unix-socket; the path matches the unit's KGSM_MONITOR_SOCKET default.
MON_SOCK="${MON_SOCK:-/run/kgsm-monitor/metrics.sock}"
HEALTH_TRIES="${HEALTH_TRIES:-30}"

# ── Helpers ─────────────────────────────────────────────────────────────────
log() { printf '\033[1;34m>> %s\033[0m\n' "$*"; }
err() { printf '\033[1;31m!! %s\033[0m\n' "$*" >&2; }

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap — attempting to bring it back up..."
        $SUDO systemctl start "$SERVICE" \
            && err "restarted ${SERVICE} (running the PREVIOUS build)." \
            || err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

wait_health() {
    local i
    for ((i = 1; i <= HEALTH_TRIES; i++)); do
        curl -fsS -o /dev/null --max-time 2 --unix-socket "$MON_SOCK" http://localhost/health 2>/dev/null && return 0
        sleep 1
    done
    return 1
}

# ── Pre-flight ────────────────────────────────────────────────────────────────
if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    err "do NOT run this as root — run it as the service user. It builds as you and sudo's only the systemd steps."
    exit 1
fi
[[ -f "$PROJECT" ]] || { err "project not found: $PROJECT"; exit 1; }
command -v clang >/dev/null 2>&1 || err "warning: 'clang' not found — Native-AOT publish needs a C toolchain (clang + zlib). Install it if publish fails."

# ── 1. Build (Native-AOT, as the invoking user — slow ILC compile) ─────────────
log "publishing Native-AOT (${RID}) → ${PUBLISH_DIR} (ILC compile — this takes a minute)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" -c Release -r "$RID" -o "$PUBLISH_DIR"

# ── 2. Privileged prep ─────────────────────────────────────────────────────────
# Install dir: create owned by the service user, or take ownership if a prior root-era install
# (the deprecated root install.sh) left it root-owned — so a redeploy on this host just works.
if [[ ! -d "$PREFIX" ]]; then
    log "creating ${PREFIX} (owned by ${SVC_USER})"
    $SUDO install -d -m 0755 -o "$SVC_USER" -g "$SVC_GROUP" "$PREFIX"
elif [[ ! -w "$PREFIX" ]]; then
    log "taking ownership of ${PREFIX} (was not writable by $(id -un))"
    $SUDO chown -R "$SVC_USER:$SVC_GROUP" "$PREFIX"
fi

# Unit: render with the service user substituted in, install only if changed.
TMP_UNIT="$(mktemp)"
sed "s/^User=.*/User=${SVC_USER}/; s/^Group=.*/Group=${SVC_GROUP}/" "$UNIT_SRC" > "$TMP_UNIT"
UNIT_CHANGED=0
if ! cmp -s "$TMP_UNIT" "$UNIT_DST"; then
    log "installing systemd unit → ${UNIT_DST} (User=${SVC_USER})"
    $SUDO install -m 0644 "$TMP_UNIT" "$UNIT_DST"
    UNIT_CHANGED=1
fi
rm -f "$TMP_UNIT"

# ── 3. The swap ────────────────────────────────────────────────────────────────
log "stopping ${SERVICE}"
$SUDO systemctl stop "$SERVICE" 2>/dev/null || true
STOPPED=1

log "installing binary → ${PREFIX}/kgsm-monitor"
install -m 0755 "$PUBLISH_DIR/kgsm-monitor" "$PREFIX/kgsm-monitor"

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    $SUDO systemctl daemon-reload
fi

log "enabling + starting ${SERVICE}"
$SUDO systemctl enable --now "$SERVICE" >/dev/null 2>&1 || $SUDO systemctl start "$SERVICE"
STOPPED=0

# ── 4. Verify (an actual "ok" from /health over the metrics socket) ────────────
log "waiting for ${SERVICE} to report healthy on ${MON_SOCK} ..."
if wait_health; then
    log "kgsm-monitor is up and healthy ✓"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service started but GET /health on ${MON_SOCK} did not return 200 within ${HEALTH_TRIES}s. Recent logs:"
    $SUDO journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi

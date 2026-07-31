#!/usr/bin/env bash
#
# deploy.sh — build + deploy the kgsm-monitor daemon. Fully headless: no sudo, no prompts.
#
#   ./deploy/deploy.sh
#
# Assumes deploy/setup.sh has provisioned this host (prefix owned by you, the unit symlinked out
# of a directory you own, polkit grant in place). If it has not, this script says so and stops
# before building. Publishes the Native-AOT binary as YOU — a single self-contained native
# binary, it needs NO .NET runtime on the host.
#
#   * the binary + its dlopen'd native libs are installed to /opt/kgsm-monitor (stale ones pruned),
#   * the systemd unit is refreshed only if it changed (a write to a file you own + daemon-reload),
#   * deploy is verified by an actual 200 from GET /health over the metrics unix socket.
#
# The monitor has no required env file: its unit's Environment= lines set sane defaults and the
# optional /etc/kgsm-monitor/kgsm-monitor.env overrides them.
#
# Knobs: RID, MON_SOCK, HEALTH_TRIES.
#
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deploy-common.sh"

PROJECT_CSPROJ="$REPO_DIR/src/Monitor/Monitor.csproj"
RID="${RID:-linux-x64}"

STOPPED=0
on_err() {
    err "deploy failed (line $1)."
    if [[ "$STOPPED" -eq 1 ]]; then
        err "the service was stopped for the swap and may be down — bringing it back up ..."
        if systemctl start "$SERVICE"; then
            err "restarted ${SERVICE} (running the PREVIOUS build)."
        else
            err "could NOT restart ${SERVICE}. Check: systemctl status ${SERVICE}"
        fi
    fi
    exit 1
}
trap 'on_err "$LINENO"' ERR

# ── Preflight ─────────────────────────────────────────────────────────────────
refuse_root
require_setup
[[ -f "$PROJECT_CSPROJ" ]] || { err "project not found: $PROJECT_CSPROJ"; exit 1; }
command -v clang >/dev/null 2>&1 || warn "'clang' not found — Native-AOT publish needs a C toolchain (clang + zlib). Install it if publish fails."

# ── 1. Build (Native-AOT, as the invoking user) ────────────────────────────────
log "publishing Native-AOT (${RID}) → ${PUBLISH_DIR} (ILC compile — this takes a minute)"
rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT_CSPROJ" -c Release -r "$RID" -o "$PUBLISH_DIR"

# ── 2. Refresh the unit if it changed (we own the file; systemd reads it via the symlink) ──
install_units_unprivileged

# ── 3. The swap ────────────────────────────────────────────────────────────────
log "stopping ${SERVICE}"
sysctl_do stop "$SERVICE" || true
STOPPED=1

log "installing binary → ${PREFIX}/kgsm-monitor"
install -m 0755 "$PUBLISH_DIR/kgsm-monitor" "$PREFIX/kgsm-monitor"

# Native shared libs the AOT binary loads at runtime via dlopen (NOT linked into the ILC output):
# SQLitePCLRaw ships libe_sqlite3.so as a separate native asset for the metrics-history store, resolved
# from the binary's own directory. Copy every .so from the publish dir beside the binary, and prune any
# that a prior build left behind but this one no longer emits, so the install tree matches the publish.
for so in "$PREFIX"/*.so; do
    [[ -e "$so" && ! -e "$PUBLISH_DIR/$(basename "$so")" ]] && { log "pruning stale native lib $(basename "$so")"; rm -f "$so"; }
done
shopt -s nullglob
for so in "$PUBLISH_DIR"/*.so; do
    log "installing native lib → ${PREFIX}/$(basename "$so")"
    install -m 0755 "$so" "$PREFIX/$(basename "$so")"
done
shopt -u nullglob

# Settings file → beside the binary, where the daemon loads it from AppContext.BaseDirectory (Program.cs).
# WITHOUT this the daemon never sees its settings (the slim builder's content root is "/" under systemd),
# so the ASP.NET log level silently stays at the chatty Information default and floods journald on every
# scrape. 0644 = world-readable, fine for shipped app defaults (operator overrides go through env vars, not
# this file); overwrite on every deploy to stay version-matched with the binary.
SETTINGS_SRC="$PUBLISH_DIR/kgsm-monitor.settings.json"
if [[ -f "$SETTINGS_SRC" ]]; then
    log "installing settings → ${PREFIX}/kgsm-monitor.settings.json"
    install -m 0644 "$SETTINGS_SRC" "$PREFIX/kgsm-monitor.settings.json"
else
    warn "${SETTINGS_SRC} not found in publish output — daemon falls back to built-in defaults."
fi

if [[ "$UNIT_CHANGED" -eq 1 ]]; then
    log "reloading systemd"
    sysctl_do daemon-reload
fi

log "starting ${SERVICE}"
sysctl_do start "$SERVICE"
STOPPED=0

# ── 4. Verify (an actual 200 from /health over the metrics socket) ─────────────
log "waiting for ${SERVICE} to report healthy on ${MON_SOCK} ..."
if wait_health; then
    log "kgsm-monitor is up and healthy ✓"
    systemctl --no-pager --lines=0 status "$SERVICE" 2>/dev/null | head -n 4 || true
else
    err "service started but GET /health on ${MON_SOCK} did not return 200 within ${HEALTH_TRIES}s. Recent logs:"
    journalctl -u "$SERVICE" -n 30 --no-pager || true
    exit 1
fi

#!/usr/bin/env bash
#
# net-meter-setup.sh — one-time, idempotent setup of the per-server network meter.
#
# Loads the passive eBPF cgroup/skb byte counter (bpf/net_meter.bpf.c), pins its map
# at the CONTRACT path, attaches both programs to the KGSM parent cgroup, makes the
# pinned map readable by the monitor's user, and grants the monitor cap_bpf. Re-running
# is safe: each step detects "already done" and only (re)does what's missing — in
# particular it re-attaches if kgsm.slice was torn down and recreated.
#
# MUST run as root (bpf() load/attach, setcap, bpffs perms). This is the script the
# orchestrator runs with sudo; it is wrapped by deploy/kgsm-net-meter.service so the
# attach is re-established on boot and whenever kgsm-watchdog (re)starts.
#
# ── Host prerequisites (install once) ────────────────────────────────────────────
#   Arch packages:  bpf  (provides bpftool)  +  libbpf  (provides bpf/bpf_helpers.h)
#                   and clang (already present on this host) to compile the .bpf.c.
#   Kernel:         cgroup v2 (this host), BTF at /sys/kernel/btf/vmlinux, bpffs at
#                   /sys/fs/bpf. All confirmed present.
#
# ── Contract A (FIXED — the monitor's NetworkCgroupSource depends on these) ───────
#   pin   : /sys/fs/bpf/kgsm/net_metrics   (BPF_MAP_TYPE_LRU_HASH, max_entries 1024)
#   key   : __u64 cgroup id (== cgroup dir inode)
#   value : { __u64 rx_bytes; tx_bytes; rx_pkts; tx_pkts; } (32 bytes)
#   attach: /sys/fs/cgroup/kgsm.slice, cgroup_skb/{ingress,egress}, BPF_F_ALLOW_MULTI
#
set -euo pipefail

# ── Config (override via environment if a host differs) ───────────────────────────
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BPFFS="${KGSM_BPFFS:-/sys/fs/bpf}"            # bpffs mountpoint
BPF_DIR="${KGSM_BPF_DIR:-$BPFFS/kgsm}"        # our pin namespace
MAP_PIN="$BPF_DIR/net_metrics"                # CONTRACT path — must not change
PROG_DIR="$BPF_DIR/progs"                     # where the two programs are pinned
SLICE="${KGSM_SLICE:-/sys/fs/cgroup/kgsm.slice}"   # the stable parent cgroup
MONITOR_BIN="${KGSM_MONITOR_BIN:-/opt/kgsm-monitor/kgsm-monitor}"
MONITOR_USER="${KGSM_MONITOR_USER:-heisen}"   # the user the monitor runs as (reads the map)

# The eBPF source + (optional) prebuilt object. Searched relative to this script first,
# then a couple of install locations, so the script works from the source tree AND once
# deployed beside the binary.
SRC=""
OBJ=""
for cand in \
    "$HERE/../bpf/net_meter.bpf.c" \
    "$HERE/net_meter.bpf.c" \
    "/opt/kgsm-monitor/net_meter.bpf.c"; do
    [[ -f "$cand" ]] && { SRC="$cand"; break; }
done
for cand in \
    "${KGSM_BPF_OBJ:-}" \
    "$HERE/../bpf/net_meter.bpf.o" \
    "$HERE/net_meter.bpf.o" \
    "/opt/kgsm-monitor/net_meter.bpf.o"; do
    [[ -n "$cand" && -f "$cand" ]] && { OBJ="$cand"; break; }
done

log()  { printf '>> %s\n' "$*"; }
warn() { printf '!! %s\n' "$*" >&2; }
die()  { warn "$*"; exit 1; }

[[ "${EUID:-$(id -u)}" -eq 0 ]] || die "must run as root (bpf load/attach, setcap, bpffs perms)"
command -v bpftool >/dev/null 2>&1 || die "bpftool not found — install the Arch 'bpf' package (pacman -S bpf)"

# ── 0. bpffs ───────────────────────────────────────────────────────────────────────
if ! mountpoint -q "$BPFFS"; then
    log "mounting bpffs at $BPFFS"
    mount -t bpf bpf "$BPFFS"
fi

# ── 1. compile (or reuse a prebuilt object) ──────────────────────────────────────────
# Prefer a shipped .o; otherwise compile the .c with clang. We only need the object
# transiently — once bpftool loads it the kernel holds the programs (pinned), so a
# freshly compiled object is staged in a tempfile and removed afterwards.
TMP_OBJ=""
cleanup() { [[ -n "$TMP_OBJ" && -f "$TMP_OBJ" ]] && rm -f "$TMP_OBJ"; }
trap cleanup EXIT

if [[ -z "$OBJ" ]]; then
    [[ -n "$SRC" ]] || die "neither a prebuilt net_meter.bpf.o nor net_meter.bpf.c was found"
    command -v clang >/dev/null 2>&1 || die "clang not found and no prebuilt .o — install clang or ship net_meter.bpf.o"
    TMP_OBJ="$(mktemp --suffix=.bpf.o)"
    OBJ="$TMP_OBJ"
    log "compiling $SRC -> $OBJ"
    # -g emits BTF so the typed map (__type key/value) loads. -target bpf, no CO-RE
    # (uapi struct __sk_buff only), so no vmlinux.h is needed.
    clang -O2 -g -target bpf -c "$SRC" -o "$OBJ"
else
    log "using prebuilt object $OBJ"
fi

# ── 2. load + pin (only when not already loaded) ─────────────────────────────────────
# The map pin existing is our "already loaded" sentinel: a pinned map (and the pinned
# programs) persist across our process exit. If it's missing we do a clean load.
mkdir -p "$BPF_DIR"
if [[ ! -e "$MAP_PIN" ]]; then
    log "loading programs + pinning map at $MAP_PIN"
    # Stale prog pins from a half-finished previous run would make loadall fail; clear them.
    rm -rf "$PROG_DIR"
    # loadall pins each program at $PROG_DIR/<func> and each map at $BPF_DIR/<mapname>,
    # so the map lands at $BPF_DIR/net_metrics (== MAP_PIN, the contract path).
    bpftool prog loadall "$OBJ" "$PROG_DIR" pinmaps "$BPF_DIR"
    [[ -e "$MAP_PIN" ]] || die "map did not pin at $MAP_PIN (map name mismatch?)"
else
    log "map already pinned at $MAP_PIN — skipping load"
    # If the map is pinned but the program pins vanished (manual rmdir), reload cleanly.
    if [[ ! -e "$PROG_DIR/count_ingress" || ! -e "$PROG_DIR/count_egress" ]]; then
        warn "program pins missing under $PROG_DIR but map is pinned"
        warn "remove the stale state to rebuild:  rm -rf '$BPF_DIR'  then re-run"
        die  "inconsistent pin state"
    fi
fi

# ── 3. attach to kgsm.slice (idempotent; re-attaches if the slice was recreated) ─────
[[ -d "$SLICE" ]] || die "$SLICE does not exist — is kgsm-watchdog running? (this unit is ordered After it)"

# A cgroup_skb attach is bound to the cgroup; re-attaching the SAME program to the SAME
# cgroup returns -EINVAL (kernel rejects the duplicate), so we must NOT blindly re-attach.
# Check the current attachments by program name and attach only the missing direction —
# which naturally re-attaches after a kgsm.slice teardown/recreate (fresh cgroup, no progs).
attached_names() { bpftool cgroup show "$SLICE" 2>/dev/null | awk 'NR>1 {print $NF}'; }

ensure_attached() {  # <attach-type> <prog-name>
    local atype="$1" name="$2"
    if attached_names | grep -qx "$name"; then
        log "$atype already attached ($name)"
        return 0
    fi
    log "attaching $atype ($name) to $SLICE with multi"
    bpftool cgroup attach "$SLICE" "$atype" pinned "$PROG_DIR/$name" multi
}

ensure_attached ingress count_ingress
ensure_attached egress  count_egress

# ── 4. let the monitor's user read the pinned map ────────────────────────────────────
# The monitor (User=$MONITOR_USER, cap_bpf, no DAC override) must (a) traverse the bpffs
# path and (b) open the map pin RW (BPF_OBJ_GET with flags 0 needs MAY_READ|MAY_WRITE on
# the pin). So: make the bpffs root searchable, own the kgsm/ dir + map pin to the user.
# The program pins under progs/ stay root-owned — the monitor never opens them.
chmod 1755 "$BPFFS"                  # +o search so $MONITOR_USER can traverse (keeps the sticky bit)
chown "$MONITOR_USER":"$MONITOR_USER" "$BPF_DIR"
chmod 0750 "$BPF_DIR"
chown "$MONITOR_USER":"$MONITOR_USER" "$MAP_PIN"
chmod 0640 "$MAP_PIN"                # owner rw (RW open), nothing for others
log "map $MAP_PIN owned by $MONITOR_USER (0640), $BPFFS searchable"

# ── 5. grant the monitor cap_bpf ──────────────────────────────────────────────────────
# unprivileged_bpf_disabled=2 on this host blocks bpf() for an uncapped process. cap_bpf
# (read-flavoured; cannot modify networking, isn't root) lets the monitor call BPF_OBJ_GET
# + BPF_MAP_LOOKUP_ELEM. NOTE: file caps are IGNORED when the service runs under systemd
# with NoNewPrivileges=true — there the unit's AmbientCapabilities=CAP_BPF supplies the cap.
# This setcap is what lets the binary run cap'd OUTSIDE systemd (manual/standalone testing),
# and documents the privilege the monitor needs. Re-run this script after any monitor
# redeploy: replacing the binary file drops its file capabilities.
if [[ -x "$MONITOR_BIN" ]]; then
    if command -v setcap >/dev/null 2>&1; then
        setcap cap_bpf+ep "$MONITOR_BIN"
        log "granted cap_bpf+ep to $MONITOR_BIN"
    else
        warn "setcap not found (install libcap) — monitor file-cap NOT set (systemd AmbientCapabilities still works)"
    fi
else
    warn "monitor binary $MONITOR_BIN not found — skipping setcap (set KGSM_MONITOR_BIN or redeploy then re-run)"
fi

log "net meter ready. Verify with:  bpftool map dump pinned $MAP_PIN"

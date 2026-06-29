// SPDX-License-Identifier: GPL-3.0-only
//
// kgsm-monitor — per-server network meter.
//
// A passive eBPF cgroup/skb byte counter. It is attached ONCE (at setup, by
// deploy/net-meter-setup.sh) to the KGSM parent cgroup `/sys/fs/cgroup/kgsm.slice`
// for both cgroup_skb/ingress and cgroup_skb/egress with BPF_F_ALLOW_MULTI, so it
// coexists with any other cgroup-BPF (e.g. systemd's) and is inherited by every
// present and future instance cgroup beneath the slice. The monitor reads the
// pinned map each tick (see src/Monitor/Sampling/NetworkCgroupSource.cs).
//
// "Passive" is the whole point: every packet is COUNTED and then ALLOWED. The
// program never drops, modifies, or reroutes traffic — it always `return 1`. This
// fits the monitor's "observe, never interfere, never fabricate" ethos and means
// instances run byte-for-byte as they do today.
//
// Keyed by cgroup id, which in cgroup v2 equals the cgroup directory's inode
// (`stat().st_ino`): `bpf_skb_cgroup_id(skb)` returns the id of the cgroup the
// socket belongs to (the instance's leaf cgroup, a descendant of kgsm.slice), and
// the monitor computes the same number by stat()-ing that directory. They match.
//
// ┌─ Contract A (FIXED — the .NET reader parses this byte-for-byte) ─────────────┐
// │ pin path : /sys/fs/bpf/kgsm/net_metrics                                      │
// │ map      : BPF_MAP_TYPE_LRU_HASH, max_entries 1024                           │
// │ key      : __u64 cgroup id (== cgroup dir inode)                             │
// │ value    : struct { __u64 rx_bytes; tx_bytes; rx_pkts; tx_pkts; } (32 bytes) │
// │ attach   : kgsm.slice, cgroup_skb/{ingress,egress}, BPF_F_ALLOW_MULTI        │
// └──────────────────────────────────────────────────────────────────────────────┘
//
// Build (done by the setup script, or locally to verify): clang must target bpf
// and emit BTF (-g) so the typed map loads:
//   clang -O2 -g -target bpf -c net_meter.bpf.c -o net_meter.bpf.o
// Needs the Arch `libbpf` package (provides bpf/bpf_helpers.h) and kernel headers
// (linux/bpf.h). No vmlinux.h / CO-RE: we touch only uapi struct __sk_buff fields
// (len), so there are no CO-RE relocations to resolve.

#include <linux/bpf.h>
#include <bpf/bpf_helpers.h>

char LICENSE[] SEC("license") = "GPL";

// Per-cgroup totals. Field order + widths are the FIXED wire contract (Contract A):
// NetworkCgroupSource reads this exact 32-byte little-endian layout. Do NOT reorder
// or change widths without bumping the contract on both sides. The monitor only reads
// rx_bytes/tx_bytes today (→ RxBps/TxBps); rx_pkts/tx_pkts are reserved for a later
// additive (pps) and are counted now so the data is already on the wire.
struct net_metrics {
    __u64 rx_bytes;
    __u64 tx_bytes;
    __u64 rx_pkts;
    __u64 tx_pkts;
};

// Pinned, keyed by cgroup id. LRU auto-evicts the oldest entries, so a long-lived
// host that installs/uninstalls many instances never overflows the 1024-entry table
// with dead cgroup ids. The map's C name (`net_metrics`) is the pinned filename when
// the loader pins by name, giving the contract path /sys/fs/bpf/kgsm/net_metrics.
struct {
    __uint(type, BPF_MAP_TYPE_LRU_HASH);
    __uint(max_entries, 1024);
    __type(key, __u64);
    __type(value, struct net_metrics);
} net_metrics SEC(".maps");

// Count one skb against its cgroup id, then always allow it. `egress` selects which
// direction's counters to bump. Marked __always_inline so both program entry points
// share one verified body (cgroup_skb programs cannot call non-inlined subprograms
// on all kernels — inlining keeps it portable).
static __always_inline int account(struct __sk_buff *skb, int egress)
{
    __u64 id = bpf_skb_cgroup_id(skb);          // the socket's cgroup -> the instance cgroup

    struct net_metrics *v = bpf_map_lookup_elem(&net_metrics, &id);
    if (!v) {
        // First sight of this cgroup: create a zeroed row. BPF_NOEXIST tolerates the
        // race where two CPUs insert concurrently (the loser gets EEXIST, harmless).
        struct net_metrics zero = {};
        bpf_map_update_elem(&net_metrics, &id, &zero, BPF_NOEXIST);
        v = bpf_map_lookup_elem(&net_metrics, &id);
        if (!v)
            return 1;                            // map full / transient failure: still allow
    }

    if (egress) {
        __sync_fetch_and_add(&v->tx_bytes, skb->len);
        __sync_fetch_and_add(&v->tx_pkts, 1);
    } else {
        __sync_fetch_and_add(&v->rx_bytes, skb->len);
        __sync_fetch_and_add(&v->rx_pkts, 1);
    }

    return 1;                                    // ALWAYS allow — this meter never drops
}

SEC("cgroup_skb/ingress")
int count_ingress(struct __sk_buff *skb)
{
    return account(skb, 0);
}

SEC("cgroup_skb/egress")
int count_egress(struct __sk_buff *skb)
{
    return account(skb, 1);
}

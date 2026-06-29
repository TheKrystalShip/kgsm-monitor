using System.Globalization;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Reads cumulative per-container network bytes from the container's OWN network namespace.
/// Container instances run in their own netns on Docker's bridge, so the eBPF <c>cgroup/skb</c>
/// meter attached to <c>kgsm.slice</c> never sees them (their cgroup lives under Docker's
/// hierarchy, e.g. <c>system.slice/docker-&lt;id&gt;.scope</c>) — this is their network source.
/// <para>
/// Given the container's resolved cgroup directory, it takes the first live pid from that
/// cgroup's <c>cgroup.procs</c> and sums the rx/tx byte counters of every non-loopback interface
/// in <c>/proc/&lt;pid&gt;/net/dev</c>. Because <c>/proc/&lt;pid&gt;/net</c> reflects that pid's
/// network namespace, the numbers are exactly the container's own traffic from its own
/// perspective (rx = received, tx = sent — no host-veth mirroring). <see cref="CgroupSampler"/>
/// turns the cumulative totals into <c>RxBps</c>/<c>TxBps</c> rates against the previous sample,
/// exactly like the native (eBPF) path and the io counters.
/// </para>
/// <para>
/// <b>Privilege: none beyond what the monitor already has.</b> <c>/proc/&lt;pid&gt;/net/dev</c> is
/// world-readable (<c>-r--r--r--</c>) even for a root-owned container process, and the docker
/// scope's <c>cgroup.procs</c> is readable (validated 2026-06-29). So a container's network is
/// metered with zero caps and no docker dependency — the monitor only reads <c>/proc</c> and
/// <c>/sys</c>, as everywhere else.
/// </para>
/// <para>
/// <b>Honest null, never a fabricated 0.</b> Returns <c>null</c> when the cgroup has no live pid
/// or no <c>/proc/&lt;pid&gt;/net/dev</c> is readable. A counter reset on container restart
/// (fresh netns → lower byte totals) is absorbed by <see cref="CgroupSampler"/>'s
/// <c>Math.Max(0, delta)</c> clamp, the same as CPU. Stateless; the caller owns the rate state.
/// </para>
/// </summary>
internal static class NetworkContainerSource
{
    // Any pid in the container's cgroup shares the same netns, so /proc/<pid>/net/dev is
    // identical for all of them — the first readable one wins. Cap the attempts so a cgroup
    // churning pids can't make us stat an unbounded list in one tick.
    private const int MaxPidsToTry = 8;

    /// <summary>
    /// Cumulative <c>{rxBytes, txBytes}</c> summed over the container's non-loopback interfaces,
    /// or <c>null</c> when no pid's <c>/proc/&lt;pid&gt;/net/dev</c> is readable (see remarks).
    /// </summary>
    internal static (long RxBytes, long TxBytes)? TryRead(string cgroupDir)
    {
        if (string.IsNullOrEmpty(cgroupDir))
            return null;

        string[] pids;
        try { pids = File.ReadAllLines(Path.Combine(cgroupDir, "cgroup.procs")); }
        catch { return null; } // cgroup vanished mid-tick (teardown race)

        int tried = 0;
        foreach (string line in pids)
        {
            string pid = line.Trim();
            if (pid.Length == 0)
                continue;
            if (++tried > MaxPidsToTry)
                break;

            if (TryReadText($"/proc/{pid}/net/dev", out string dev))
                return SumNonLoopback(dev);
        }
        return null;
    }

    private static bool TryReadText(string path, out string content)
    {
        try { content = File.ReadAllText(path); return true; }
        catch { content = string.Empty; return false; }
    }

    /// <summary>
    /// Sum rx/tx bytes across every non-loopback interface of a <c>/proc/net/dev</c> body. Each
    /// data line is <c>"&lt;iface&gt;: &lt;rx_bytes&gt; &lt;rx_pkts&gt; …(8 rx cols)… &lt;tx_bytes&gt;
    /// &lt;tx_pkts&gt; …(8 tx cols)"</c>; the two header lines (no early colon) and the <c>lo</c>
    /// interface are skipped. Pure + golden-file testable.
    /// </summary>
    internal static (long RxBytes, long TxBytes) SumNonLoopback(string procNetDev)
    {
        long rx = 0, tx = 0;
        foreach (string raw in procNetDev.Split('\n'))
        {
            int colon = raw.IndexOf(':');
            if (colon < 0)
                continue; // header lines ("Inter-|…", " face |…") carry no interface colon
            string iface = raw[..colon].Trim();
            if (iface.Length == 0 || iface == "lo")
                continue;

            string[] nums = raw[(colon + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length < 9)
                continue; // not a stats line
            if (long.TryParse(nums[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long r))
                rx += r; // rx_bytes
            if (long.TryParse(nums[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out long t))
                tx += t; // tx_bytes
        }
        return (rx, tx);
    }
}

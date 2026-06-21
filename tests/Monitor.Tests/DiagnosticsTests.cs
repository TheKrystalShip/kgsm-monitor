using System.Linq;
using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Slice-1 host-diagnostics depth: static CPU identity, hwmon temperatures, per-mount
/// backing-disk model, and per-interface MAC + error counts. Pure parse helpers run
/// against captured/synthetic fixtures; the <c>/sys</c> walks run against synthetic
/// trees rooted under <c>Fixtures/sys</c> so the honesty guards (empty → <c>[]</c>,
/// unreadable → <c>null</c>) are tested without touching the live host.
/// </summary>
public class CpuInfoSourceTests
{
    [Fact]
    public void Parse_extracts_model_cores_and_threads_from_real_cpuinfo()
    {
        // Captured /proc/cpuinfo: AMD Ryzen 7 3800X, 8 cores / 16 threads, single socket.
        var info = CpuInfoSource.Parse(Fixtures.Read("cpuinfo.txt"), maxFreqGhz: 4.56);

        Assert.Equal("AMD Ryzen 7 3800X 8-Core Processor", info.Model);
        Assert.Equal(8, info.Cores);     // "cpu cores"
        Assert.Equal(16, info.Threads);  // count of "processor" lines
        Assert.Equal(4.56, info.MaxFreqGhz!.Value, 2);
    }

    [Fact]
    public void Parse_single_socket_simplification_on_a_two_socket_cpuinfo()
    {
        // Synthetic dual-socket Xeon: 2 physical ids, 8 cpu cores each, 32 processor lines.
        var info = CpuInfoSource.Parse(Fixtures.Read("cpuinfo.2socket.txt"), maxFreqGhz: null);

        // Threads stays correct across sockets (every logical CPU is a processor line).
        Assert.Equal(32, info.Threads);
        // KNOWN SIMPLIFICATION: Cores reports the FIRST socket's count (8), not the box
        // total (16). On this single-socket-assuming host that is the documented behavior;
        // pinned here so a future multi-socket fold is a deliberate, test-visible change.
        Assert.Equal(8, info.Cores);
        Assert.NotEqual(16, info.Cores); // explicitly not the summed-across-sockets value
    }

    [Fact]
    public void Parse_returns_nulls_when_cpuinfo_is_empty()
    {
        var info = CpuInfoSource.Parse(string.Empty, maxFreqGhz: null);

        Assert.Null(info.Model);
        Assert.Null(info.Cores);
        Assert.Null(info.Threads);   // no processor lines -> null, not 0
        Assert.Null(info.MaxFreqGhz);
    }

    [Fact]
    public void ParseMaxFreqGhz_converts_khz_to_ghz()
    {
        // Captured /sys cpuinfo_max_freq: 4560324 kHz -> 4.56 GHz (stable max, not jittery cpu MHz).
        Assert.Equal(4.56, CpuInfoSource.ParseMaxFreqGhz(Fixtures.Read("cpuinfo_max_freq.txt"))!.Value, 2);
        Assert.Equal(3.3, CpuInfoSource.ParseMaxFreqGhz("3300000")!.Value, 2);
    }

    [Fact]
    public void ParseMaxFreqGhz_returns_null_when_absent_or_invalid()
    {
        Assert.Null(CpuInfoSource.ParseMaxFreqGhz(null));        // cpufreq file absent
        Assert.Null(CpuInfoSource.ParseMaxFreqGhz(""));
        Assert.Null(CpuInfoSource.ParseMaxFreqGhz("not-a-number"));
        Assert.Null(CpuInfoSource.ParseMaxFreqGhz("0"));
    }
}

public class SensorSourceTests
{
    private static string HwmonRoot => Fixtures.Path("sys/hwmon");

    [Fact]
    public void Read_extracts_temperatures_with_milli_to_celsius_conversion()
    {
        var sensors = SensorSource.Read(HwmonRoot);

        // nvme temp1_input = 30900 milli-°C -> 30.9 °C, label "Composite".
        var composite = sensors.Single(s => s.Chip == "nvme" && s.Label == "Composite");
        Assert.Equal(30.9, composite.ValueC, 1);

        // k10temp Tctl = 44300 -> 44.3 °C.
        var tctl = sensors.Single(s => s.Chip == "k10temp" && s.Label == "Tctl");
        Assert.Equal(44.3, tctl.ValueC, 1);
    }

    [Fact]
    public void Read_handles_sparse_temp_indices()
    {
        // k10temp has temp1 + temp3 but NO temp2 (real AMD layout) — globbing temp*_input
        // must pick up both and not assume a contiguous range.
        var k10 = SensorSource.Read(HwmonRoot).Where(s => s.Chip == "k10temp").ToArray();

        Assert.Equal(2, k10.Length);
        Assert.Contains(k10, s => s.Label == "Tctl");
        Assert.Contains(k10, s => s.Label == "Tccd1");
    }

    [Fact]
    public void Read_leaves_label_null_when_chip_has_no_label_file()
    {
        // jc42 chips expose temp1_input with no temp1_label.
        var jc42 = SensorSource.Read(HwmonRoot).Where(s => s.Chip == "jc42").ToArray();

        Assert.NotEmpty(jc42);
        Assert.All(jc42, s => Assert.Null(s.Label));
    }

    [Fact]
    public void Read_keeps_chips_that_share_a_name_without_deduping()
    {
        // Two distinct jc42 DIMM sensors share the chip name — both must appear.
        var jc42 = SensorSource.Read(HwmonRoot).Where(s => s.Chip == "jc42").ToArray();
        Assert.Equal(2, jc42.Length);
    }

    [Fact]
    public void Read_returns_empty_array_when_no_hwmon_present()
    {
        // The honesty guard: absent tree -> [] (never an invented row).
        Assert.Empty(SensorSource.Read(Fixtures.Path("sys/hwmon-empty")));
        Assert.Empty(SensorSource.Read(Fixtures.Path("sys/does-not-exist")));
    }
}

public class DiskDeviceJoinTests
{
    private static string SysBlock => Fixtures.Path("sys/block");

    [Theory]
    [InlineData("/dev/nvme0n1p2", "nvme0n1")]  // nvme: p<N> partition infix
    [InlineData("/dev/sda1", "sda")]            // sd: trailing partition digits
    public void NormalizePartitionToDisk_maps_partitions_to_their_whole_disk(string dev, string expected)
    {
        Assert.Equal(expected, DiskSource.NormalizePartitionToDisk(dev, SysBlock));
    }

    [Fact]
    public void NormalizePartitionToDisk_returns_whole_disk_node_unchanged()
    {
        // A node that already names a whole disk (its /sys/block dir exists) is kept as-is.
        Assert.Equal("nvme0n1", DiskSource.NormalizePartitionToDisk("/dev/nvme0n1", SysBlock));
    }

    [Fact]
    public void NormalizePartitionToDisk_returns_null_for_unresolvable_nodes()
    {
        // device-mapper / LVM nodes have no /sys/block disk -> null, never a guess.
        Assert.Null(DiskSource.NormalizePartitionToDisk("/dev/mapper/vg-root", SysBlock));
        Assert.Null(DiskSource.NormalizePartitionToDisk("/dev/sdz9", SysBlock)); // sdz absent from /sys/block
    }

    [Fact]
    public void ReadDiskModel_reads_the_model_string()
    {
        Assert.Equal("Samsung SSD 990 EVO Plus 1TB", DiskSource.ReadDiskModel(SysBlock, "nvme0n1"));
        Assert.Equal("WDC WD40EFRX-68N32N0", DiskSource.ReadDiskModel(SysBlock, "sda"));
    }

    [Fact]
    public void ReadDiskModel_returns_null_when_model_file_absent()
    {
        // The honesty guard: a disk dir with no model file -> null.
        Assert.Null(DiskSource.ReadDiskModel(SysBlock, "nomodel"));
    }

    [Fact]
    public void ParseMountInfo_extracts_real_block_mounts_after_the_dash_separator()
    {
        // Captured /proc/self/mountinfo: source (/dev node) is the 2nd token after " - ".
        var mounts = DiskSource.ParseMountInfo(Fixtures.Read("mountinfo.txt")).ToDictionary(m => m.MountPoint, m => m.DevNode);

        Assert.Equal("/dev/nvme0n1p2", mounts["/"]);
        Assert.Equal("/dev/nvme0n1p1", mounts["/boot"]);
        // Pseudo/tmpfs mounts (source not /dev/...) are excluded.
        Assert.DoesNotContain("/dev/shm", mounts.Keys);
    }

    [Fact]
    public void BuildDeviceModels_joins_mount_to_backing_disk_model()
    {
        // End-to-end of the one non-flat piece: mountinfo (mount -> /dev) joined onto
        // /sys/block/<disk>/device/model. Both real mounts sit on the same nvme disk, so
        // both carry the same model — that is correct, not a duplicate.
        var map = DiskSource.BuildDeviceModels(Fixtures.Path("mountinfo.txt"), SysBlock);

        Assert.Equal("Samsung SSD 990 EVO Plus 1TB", map["/"]);
        Assert.Equal("Samsung SSD 990 EVO Plus 1TB", map["/boot"]);
    }

    [Fact]
    public void BuildDeviceModels_returns_empty_when_mountinfo_absent()
    {
        var map = DiskSource.BuildDeviceModels(Fixtures.Path("does-not-exist"), SysBlock);
        Assert.Empty(map);
    }
}

public class NetworkEnrichmentTests
{
    private static string NetRoot => Fixtures.Path("sys/net");

    [Fact]
    public void ReadMac_reads_the_interface_address()
    {
        Assert.Equal("9c:6b:00:27:39:6d", NetworkSource.ReadMac(NetRoot, "enp4s0"));
        Assert.Equal("e2:6c:df:bd:0d:cf", NetworkSource.ReadMac(NetRoot, "wlp5s0"));
    }

    [Fact]
    public void ReadMac_returns_null_when_address_absent()
    {
        Assert.Null(NetworkSource.ReadMac(NetRoot, "does-not-exist"));
    }

    [Fact]
    public void ReadErrors_sums_rx_and_tx_error_counters()
    {
        // enp4s0: rx_errors 5 + tx_errors 2 = 7.
        Assert.Equal(7, NetworkSource.ReadErrors(NetRoot, "enp4s0"));
        // wlp5s0: a genuine zero stays 0 (measured, not "unknown").
        Assert.Equal(0, NetworkSource.ReadErrors(NetRoot, "wlp5s0"));
    }

    [Fact]
    public void ReadErrors_returns_null_when_no_statistics_present()
    {
        // The honesty guard: neither rx nor tx error file -> null, never a fabricated 0.
        Assert.Null(NetworkSource.ReadErrors(NetRoot, "nostats"));
        Assert.Null(NetworkSource.ReadErrors(NetRoot, "does-not-exist"));
    }
}

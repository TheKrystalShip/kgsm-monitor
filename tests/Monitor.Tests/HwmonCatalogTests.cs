using TheKrystalShip.KGSM.Monitor.Sampling;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Builds a synthetic <c>/sys/class/hwmon</c> tree at run time. The <c>device</c> entry has to be a real
/// symlink for <see cref="SensorSource"/> to resolve a device key off it, and the checked-in fixture tree
/// is copied by an MSBuild glob that would follow the link and flatten it — so a tree that needs one is
/// made here instead. It lands under the run's own temp root, which the run takes back on the way out.
/// </summary>
internal sealed class HwmonTree : IDisposable
{
    private readonly string _devices;

    public string Root { get; }

    public HwmonTree()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "hwmon-" + Guid.NewGuid().ToString("n")[..8]);
        Root = Path.Combine(baseDir, "class", "hwmon");
        _devices = Path.Combine(baseDir, "devices");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(_devices);
    }

    /// <summary>
    /// Add a chip. <paramref name="device"/> becomes a real <c>device</c> symlink when given; the channels
    /// are <c>("temp1", 44300, "Tctl")</c>-style triples of file stem, raw value and optional label.
    /// </summary>
    public HwmonTree Chip(string hwmonDir, string name, string? device,
        params (string Channel, long Raw, string? Label)[] channels)
    {
        string dir = Path.Combine(Root, hwmonDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "name"), name + "\n");

        if (device is not null)
        {
            string target = Path.Combine(_devices, device);
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(Path.Combine(dir, "device"), target);
        }

        foreach ((string channel, long raw, string? label) in channels)
        {
            File.WriteAllText(Path.Combine(dir, channel + "_input"), raw + "\n");
            if (label is not null) File.WriteAllText(Path.Combine(dir, channel + "_label"), label + "\n");
        }

        return this;
    }

    /// <summary>Attach hwmon's published limit files to a channel — <c>tempN_max</c> / <c>tempN_crit</c>.</summary>
    public HwmonTree Limit(string hwmonDir, string channel, long? max = null, long? crit = null)
    {
        string dir = Path.Combine(Root, hwmonDir);
        if (max is { } m) File.WriteAllText(Path.Combine(dir, channel + "_max"), m + "\n");
        if (crit is { } c) File.WriteAllText(Path.Combine(dir, channel + "_crit"), c + "\n");
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Root)!)!, recursive: true); }
        catch { /* the run's temp root sweep is the backstop */ }
    }
}

public class HwmonIdentityTests
{
    [Fact]
    public void Chips_sharing_a_name_are_separated_by_their_device()
    {
        // The exact collision the threshold fan-out could not resolve: two DDR4 DIMMs, both chip "jc42",
        // both unlabelled, distinguishable only by the i2c address behind the device link.
        using var tree = new HwmonTree();
        tree.Chip("hwmon2", "jc42", "0-0018", ("temp1", 33500, null))
            .Chip("hwmon3", "jc42", "0-0019", ("temp1", 41000, null));

        var readings = SensorSource.Read(tree.Root).Temps;

        Assert.Equal(2, readings.Length);
        Assert.Equal(2, readings.Select(r => r.Id).Distinct().Count());
        Assert.Contains(readings, r => r.Id == "jc42/0-0018/temp1");
        Assert.Contains(readings, r => r.Id == "jc42/0-0019/temp1");
    }

    [Fact]
    public void Ordinals_follow_the_device_key_not_the_hwmon_index()
    {
        // The hwmon index moves between boots with the order buses bind, so the same DIMM has to keep its
        // name when the kernel hands it a different number. Here the indices are swapped relative to the
        // addresses: module 1 must still be the chip at 0-0018.
        using var tree = new HwmonTree();
        tree.Chip("hwmon9", "jc42", "0-0018", ("temp1", 33500, null))
            .Chip("hwmon1", "jc42", "0-0019", ("temp1", 41000, null));

        var readings = SensorSource.Read(tree.Root).Temps;

        Assert.Equal("Memory module 1", readings.Single(r => r.Id.Contains("0-0018")).Name);
        Assert.Equal("Memory module 2", readings.Single(r => r.Id.Contains("0-0019")).Name);
    }

    [Fact]
    public void A_lone_chip_of_its_kind_is_not_numbered()
    {
        using var tree = new HwmonTree();
        tree.Chip("hwmon0", "nvme", "nvme0", ("temp1", 48900, "Composite"));

        Assert.Equal("SSD", SensorSource.Read(tree.Root).Temps.Single().Name);
    }

    [Fact]
    public void Device_key_falls_back_to_the_directory_when_there_is_no_link()
    {
        using var tree = new HwmonTree();
        tree.Chip("hwmon4", "iwlwifi_1_0", device: null, ("temp1", 45000, null));

        Assert.Equal("iwlwifi_1_0/hwmon4/temp1", SensorSource.Read(tree.Root).Temps.Single().Id);
    }
}

public class HwmonNamingTests
{
    [Theory]
    [InlineData("k10temp", "Tctl", "cpu", "CPU")]
    [InlineData("k10temp", "Tdie", "cpu", "CPU")]
    [InlineData("k10temp", "Tccd1", "cpu", "CPU die 1")]
    [InlineData("coretemp", "Package id 0", "cpu", "CPU")]
    [InlineData("coretemp", "Core 5", "cpu", "CPU core 5")]
    [InlineData("nvme", "Composite", "drive", "SSD")]
    [InlineData("nvme", "Sensor 2", "drive", "SSD sensor 2")]
    [InlineData("drivetemp", null, "drive", "Drive")]
    [InlineData("jc42", null, "memory", "Memory module")]
    [InlineData("amdgpu", "junction", "gpu", "GPU junction")]
    [InlineData("iwlwifi_1_0", null, "network", "Wi-Fi adapter")]
    [InlineData("nct6792", "CPUTIN", "board", "CPU socket")]
    [InlineData("nct6792", "SYSTIN", "board", "System")]
    [InlineData("nct6792", "AUXTIN0", "board", "Auxiliary sensor 0")]
    [InlineData("nct6792", "TSI0_TEMP", "cpu", "CPU via TSI0")]
    [InlineData("nct6792", "SMBUSMASTER 0", "cpu", "CPU via SMBus 0")]
    [InlineData("nct6792", "PCH_CHIP_TEMP", "chipset", "Chipset")]
    public void Known_channels_get_a_role_and_a_human_name(string chip, string? label, string role, string name)
    {
        (string? gotRole, string? gotName) = HwmonCatalog.Classify(chip, label, ordinal: 1, total: 1);

        Assert.Equal(role, gotRole);
        Assert.Equal(name, gotName);
    }

    [Fact]
    public void Registers_relaying_the_same_cpu_reading_are_named_apart()
    {
        // A board publishes the CPU's control temperature under more than one register. Named alike they
        // render as two identical rows and read as a duplicated bug; the path is what tells them apart.
        (_, string? tsi) = HwmonCatalog.Classify("nct6792", "TSI0_TEMP", 1, 1);
        (_, string? smbus) = HwmonCatalog.Classify("nct6792", "SMBUSMASTER 0", 1, 1);

        Assert.NotEqual(tsi, smbus);
    }

    [Fact]
    public void A_name_states_the_thing_measured_not_the_quantity()
    {
        // The record already carries °C, so a name ending in "temperature" repeats the type and reads as
        // noise under a heading that says Temperatures.
        foreach (string label in new[] { "Tctl", "Tccd1" })
        {
            (_, string? name) = HwmonCatalog.Classify("k10temp", label, 1, 1);
            Assert.DoesNotContain("temperature", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_unknown_chip_is_reported_unclassified_rather_than_dropped()
    {
        // A host whose hardware is not in the table keeps every reading it had; it only gains no name.
        (string? role, string? name) = HwmonCatalog.Classify("some_new_chip", "temp1", ordinal: 1, total: 1);
        Assert.Null(role);
        Assert.Null(name);

        using var tree = new HwmonTree();
        tree.Chip("hwmon0", "some_new_chip", "pci0000:99", ("temp1", 51000, null));

        var reading = SensorSource.Read(tree.Root).Temps.Single();
        Assert.Equal(51.0, reading.ValueC);
        Assert.Equal("some_new_chip", reading.Chip);
        Assert.Null(reading.Role);
        Assert.Null(reading.Name);
    }

    [Fact]
    public void A_fan_is_named_by_its_label_when_the_driver_publishes_one()
    {
        // Which header drives which physical fan is board wiring that hwmon does not carry, so a label is
        // the board's own answer and outranks the number.
        Assert.Equal("Fan 1", HwmonCatalog.NameFan(null, 1));
        Assert.Equal("Fan 3", HwmonCatalog.NameFan("   ", 3));
        Assert.Equal("CPU Fan", HwmonCatalog.NameFan("CPU Fan", 1));
    }
}

public class SuperIoFilterTests
{
    [Theory]
    [InlineData("SYSTIN", 113.0)]      // pegged: an unwired thermistor input sitting at the rail
    [InlineData("AUXTIN1", 104.0)]
    [InlineData("AUXTIN3", -128.0)]    // the driver's open-circuit sentinel
    [InlineData("CPUTIN", 0.0)]
    public void Unconnected_super_io_pins_are_withheld(string label, double valueC)
        => Assert.False(HwmonCatalog.IsReal("nct6792", label, valueC));

    [Theory]
    [InlineData("CPUTIN", 44.5)]
    [InlineData("AUXTIN0", 43.0)]
    [InlineData("SYSTIN", 31.0)]
    public void Board_thermistors_inside_the_physical_band_are_kept(string label, double valueC)
        => Assert.True(HwmonCatalog.IsReal("nct6792", label, valueC));

    [Fact]
    public void Pch_registers_are_withheld_only_when_they_read_a_hard_zero()
    {
        // The driver publishes the Intel platform-controller registers everywhere; off an Intel PCH they
        // read exactly zero. A populated one is a real chipset temperature and stays.
        Assert.False(HwmonCatalog.IsReal("nct6792", "PCH_CHIP_TEMP", 0.0));
        Assert.True(HwmonCatalog.IsReal("nct6792", "PCH_CHIP_TEMP", 48.0));
    }

    [Fact]
    public void Channels_relaying_silicon_are_exempt_from_the_thermistor_band()
    {
        // TSI0/SMBUSMASTER carry the CPU's own control temperature over SB-TSI. That is silicon and may sit
        // above the band a board-mounted thermistor lives in, so the range test must not touch it.
        Assert.True(HwmonCatalog.IsReal("nct6792", "TSI0_TEMP", 101.5));
        Assert.True(HwmonCatalog.IsReal("nct6792", "SMBUSMASTER 0", 104.0));
    }

    [Fact]
    public void Silicon_chips_are_never_range_judged()
    {
        // A CPU or an NVMe legitimately runs past 100 °C. Only super-I/O has pins that lie.
        Assert.True(HwmonCatalog.IsReal("k10temp", "Tctl", 105.0));
        Assert.True(HwmonCatalog.IsReal("nvme", "Composite", 101.0));
        Assert.True(HwmonCatalog.IsReal("amdgpu", "junction", 110.0));
    }

    [Fact]
    public void The_walk_withholds_dead_pins_and_counts_them()
    {
        // The board this was measured on: one wired thermistor, one CPU relay, and five pins reporting
        // nothing. What is withheld has to be countable, or the gap between `sensors` and the panel is
        // unexplained.
        using var tree = new HwmonTree();
        tree.Chip("hwmon5", "nct6792", "nct6775.2592",
            ("temp1", 113000, "SYSTIN"),
            ("temp2", 44500, "CPUTIN"),
            ("temp4", 104000, "AUXTIN1"),
            ("temp6", -128000, "AUXTIN3"),
            ("temp9", 0, "PCH_CHIP_TEMP"),
            ("temp10", 0, "PCH_CPU_TEMP"),
            ("temp13", 59875, "TSI0_TEMP"));

        HwmonSample sample = SensorSource.Read(tree.Root);

        Assert.Equal(5, sample.WithheldChannels);
        Assert.Equal(
            ["CPU socket", "CPU via TSI0"],
            sample.Temps.Select(t => t.Name).Order());
    }
}

public class FanReadingTests
{
    [Fact]
    public void Turning_fans_are_reported_and_still_tachometers_are_not()
    {
        // Boards publish more headers than anyone populates; an empty header and a stopped fan both read
        // zero and hwmon cannot separate them, so a zero is never asserted as a fan that exists.
        using var tree = new HwmonTree();
        tree.Chip("hwmon5", "nct6792", "nct6775.2592",
            ("fan1", 2406, null), ("fan2", 0, null), ("fan3", 0, null), ("fan4", 1180, "Chassis Fan"));

        var fans = SensorSource.Read(tree.Root).Fans;

        Assert.Equal(2, fans.Length);
        Assert.Equal(2406, fans.Single(f => f.Id.EndsWith("fan1")).Rpm);
        Assert.Equal("Fan 1", fans.Single(f => f.Id.EndsWith("fan1")).Name);
        Assert.Equal("Chassis Fan", fans.Single(f => f.Id.EndsWith("fan4")).Name);
    }

    [Fact]
    public void Fans_and_temperatures_come_out_of_one_walk()
    {
        using var tree = new HwmonTree();
        tree.Chip("hwmon5", "nct6792", "nct6775.2592", ("temp2", 44500, "CPUTIN"), ("fan1", 2406, null));

        HwmonSample sample = SensorSource.Read(tree.Root);

        Assert.Single(sample.Temps);
        Assert.Single(sample.Fans);
    }

    [Fact]
    public void No_hwmon_tree_yields_no_fans()
        => Assert.Empty(SensorSource.Read(Path.Combine(Path.GetTempPath(), "definitely-not-here")).Fans);
}

public class PublishedLimitTests
{
    [Theory]
    [InlineData(80.85)]   // an NVMe warning threshold
    [InlineData(84.85)]   // its critical
    [InlineData(80.0)]    // a super-I/O tempN_max
    [InlineData(100.0)]   // an Intel Tjmax
    public void A_plausible_limit_is_kept(double c) => Assert.Equal(c, HwmonCatalog.PlausibleLimit(c));

    [Theory]
    [InlineData(0.0)]        // the register was never populated — jc42 publishes max=0 AND crit=0
    [InlineData(65261.85)]   // NVMe carries Kelvin; 0xFFFF K is the spec's "unimplemented"
    [InlineData(-273.1)]     // the floor of the same encoding
    [InlineData(39.9)]       // nothing shuts down below 40 °C; such a limit is breached at idle
    [InlineData(150.1)]
    public void An_implausible_limit_is_no_limit(double c) => Assert.Null(HwmonCatalog.PlausibleLimit(c));

    [Fact]
    public void A_missing_limit_stays_missing() => Assert.Null(HwmonCatalog.PlausibleLimit(null));

    [Fact]
    public void An_inverted_pair_drops_its_critical()
    {
        // A critical below the warning line means at least one register is not what it claims. Keeping it
        // would fire the more severe band first, which is the wrong way to be wrong.
        (double? high, double? crit) = HwmonCatalog.Limits(90.0, 70.0);
        Assert.Equal(90.0, high);
        Assert.Null(crit);
    }

    [Fact]
    public void A_consistent_pair_survives_intact()
    {
        (double? high, double? crit) = HwmonCatalog.Limits(80.85, 84.85);
        Assert.Equal(80.85, high);
        Assert.Equal(84.85, crit);
    }

    [Fact]
    public void Limits_are_read_off_the_channel_and_gated()
    {
        // The NVMe layout this was measured against: a real pair on the composite, and the Kelvin
        // sentinel on the component sensors.
        using var tree = new HwmonTree();
        tree.Chip("hwmon0", "nvme", "nvme0",
            ("temp1", 48900, "Composite"), ("temp2", 49800, "Sensor 1"));
        tree.Limit("hwmon0", "temp1", max: 80850, crit: 84850);
        tree.Limit("hwmon0", "temp2", max: 65261850, crit: null);

        var temps = SensorSource.Read(tree.Root).Temps;

        SensorReadingLimits(temps, "temp1", 80.85, 84.85);
        SensorReadingLimits(temps, "temp2", null, null);
    }

    private static void SensorReadingLimits(
        TheKrystalShip.KGSM.Monitor.Contracts.SensorReading[] temps, string channel, double? high, double? crit)
    {
        var r = temps.Single(t => t.Id.EndsWith("/" + channel));
        Assert.Equal(high, r.LimitHighC);
        Assert.Equal(crit, r.LimitCriticalC);
    }
}

public class PrimaryAndDuplicateTests
{
    [Theory]
    [InlineData("k10temp", "Tctl", true)]
    [InlineData("k10temp", "Tccd1", false)]     // a breakdown of the package, not its headline
    [InlineData("coretemp", "Package id 0", true)]
    [InlineData("coretemp", "Core 3", false)]
    [InlineData("nvme", "Composite", true)]
    [InlineData("nvme", "Sensor 1", false)]
    [InlineData("jc42", null, true)]            // each DIMM is its own device
    [InlineData("nct6792", "CPUTIN", true)]
    [InlineData("nct6792", "TSI0_TEMP", false)] // the CPU's own reading, relayed
    [InlineData("some_new_chip", null, true)]   // unknown hardware is never assumed redundant
    public void Primary_marks_the_headline_channel_for_a_device(string chip, string? label, bool primary)
        => Assert.Equal(primary, HwmonCatalog.IsPrimary(chip, label));

    [Fact]
    public void A_relayed_cpu_channel_names_the_reading_it_restates()
    {
        using var tree = new HwmonTree();
        tree.Chip("hwmon1", "k10temp", "0000:00:18.3", ("temp1", 61400, "Tctl"), ("temp3", 58800, "Tccd1"))
            .Chip("hwmon5", "nct6792", "nct6775.656",
                  ("temp7", 61500, "SMBUSMASTER 0"), ("temp13", 61500, "TSI0_TEMP"), ("temp2", 46500, "CPUTIN"));

        var temps = SensorSource.Read(tree.Root).Temps;
        const string pkg = "k10temp/0000:00:18.3/temp1";

        Assert.Equal(pkg, temps.Single(t => t.Label == "TSI0_TEMP").DuplicateOf);
        Assert.Equal(pkg, temps.Single(t => t.Label == "SMBUSMASTER 0").DuplicateOf);
        // A board thermistor measures the socket, not the die — it restates nothing.
        Assert.Null(temps.Single(t => t.Label == "CPUTIN").DuplicateOf);
        Assert.Null(temps.Single(t => t.Label == "Tctl").DuplicateOf);
    }

    [Fact]
    public void With_two_cpu_packages_a_relay_is_left_unattributed()
    {
        // Two sockets and two relays: nothing in hwmon says which relay reads which package, so the link
        // is left unstated rather than guessed at.
        using var tree = new HwmonTree();
        tree.Chip("hwmon1", "coretemp", "0000:00:18.3", ("temp1", 61400, "Package id 0"))
            .Chip("hwmon2", "coretemp", "0000:00:19.3", ("temp1", 60100, "Package id 1"))
            .Chip("hwmon5", "nct6792", "nct6775.656", ("temp13", 61500, "TSI0_TEMP"));

        var temps = SensorSource.Read(tree.Root).Temps;
        Assert.Null(temps.Single(t => t.Label == "TSI0_TEMP").DuplicateOf);
    }
}

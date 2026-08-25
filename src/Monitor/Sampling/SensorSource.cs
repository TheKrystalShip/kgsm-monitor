using System.Globalization;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Hardware temperatures and fan tachometers from <c>/sys/class/hwmon/hwmon*/</c>. Each chip exposes a
/// <c>name</c>, zero or more <c>tempN_input</c> files in milli-°C and zero or more <c>fanN_input</c> files
/// in RPM, each with an optional <c>tempN_label</c>/<c>fanN_label</c>. Instantaneous (no delta). The hwmon
/// root is injectable so the walk is golden-file testable against a synthetic tree.
/// </summary>
/// <remarks>
/// <para><b>Indices are sparse</b> — k10temp has <c>temp1</c> and <c>temp3</c> and no <c>temp2</c> — so the
/// walk globs <c>temp*_input</c> rather than looping a fixed range.</para>
/// <para><b>Chips are ordered before they are named.</b> Directory enumeration order is the filesystem's,
/// and the hwmon index itself moves between boots with the order buses bind, so both are sorted away: chips
/// are ranked by name then device key, which is what makes "Memory module 1" the same DIMM tomorrow.</para>
/// <para><b>Honesty.</b> When no chip exposes a temperature the result is an empty array — never an invented
/// row. An unreadable file is skipped, not defaulted. The one thing deliberately withheld is an unconnected
/// super-I/O pin, which is not a measurement at all; <see cref="HwmonCatalog.IsReal"/> carries that argument
/// and <see cref="HwmonSample.WithheldChannels"/> counts what it withheld, so the omission is visible
/// rather than silent.</para>
/// </remarks>
public sealed class SensorSource(string hwmonRoot = "/sys/class/hwmon")
{
    private readonly string _root = hwmonRoot;

    public HwmonSample Sample() => Read(_root);

    /// <summary>Walk a hwmon root directory into readings — pure over the filesystem.</summary>
    internal static HwmonSample Read(string root)
    {
        if (!Directory.Exists(root))
            return new HwmonSample([], [], 0);

        // Pass one: identify every chip, so the ordinals handed to the catalog are stable and so a chip
        // knows how many namesakes it has before anything is named.
        var chips = new List<ChipDir>();
        foreach (string dir in Directory.GetDirectories(root))
        {
            string name = ReadTrimmed(Path.Combine(dir, "name")) ?? Path.GetFileName(dir);
            chips.Add(new ChipDir(dir, name, DeviceKey(dir)));
        }

        chips.Sort(static (a, b) =>
        {
            int byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : string.CompareOrdinal(a.Device, b.Device);
        });

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ChipDir chip in chips)
            byName[chip.Name] = byName.GetValueOrDefault(chip.Name) + 1;

        var seenSoFar = new Dictionary<string, int>(StringComparer.Ordinal);
        var temps = new List<SensorReading>();
        var fans = new List<FanReading>();
        int withheld = 0;

        foreach (ChipDir chip in chips)
        {
            int ordinal = seenSoFar[chip.Name] = seenSoFar.GetValueOrDefault(chip.Name) + 1;
            int total = byName[chip.Name];

            foreach (string inputPath in Sorted(chip.Dir, "temp*_input"))
            {
                if (!TryReadLong(inputPath, out long milliC))
                    continue; // unreadable / non-numeric -> skip, never fabricate

                string channel = Channel(inputPath);
                string? label = ReadTrimmed(Path.Combine(chip.Dir, channel + "_label"));
                double valueC = Math.Round(milliC / 1000.0, 1);

                if (!HwmonCatalog.IsReal(chip.Name, label, valueC))
                {
                    withheld++;
                    continue;
                }

                (string? role, string? name) = HwmonCatalog.Classify(chip.Name, label, ordinal, total);

                // The device's own opinion of hot, when it has one. `crit` is the throttle/shutdown line;
                // `emergency` stands in where a driver publishes that instead. Both go through the same
                // plausibility gate — the ABI gives no way to tell an unset register from a setting.
                (double? high, double? critical) = HwmonCatalog.Limits(
                    ReadMilliC(chip.Dir, channel, "max"),
                    ReadMilliC(chip.Dir, channel, "crit") ?? ReadMilliC(chip.Dir, channel, "emergency"));

                temps.Add(new SensorReading(
                    Id: $"{chip.Name}/{chip.Device}/{channel}",
                    Chip: chip.Name,
                    Label: label,
                    ValueC: valueC,
                    Role: role,
                    Name: name,
                    LimitHighC: high,
                    LimitCriticalC: critical,
                    Primary: HwmonCatalog.IsPrimary(chip.Name, label)));
            }

            foreach (string inputPath in Sorted(chip.Dir, "fan*_input"))
            {
                if (!TryReadLong(inputPath, out long rpm))
                    continue;

                // A header with nothing plugged in and a fan that has stopped both read zero, and hwmon
                // offers nothing to separate them. Boards publish far more headers than anyone populates,
                // so a zero is dropped rather than asserted as a fan that exists and is not turning.
                if (rpm <= 0)
                    continue;

                string channel = Channel(inputPath);
                string? label = ReadTrimmed(Path.Combine(chip.Dir, channel + "_label"));
                fans.Add(new FanReading(
                    Id: $"{chip.Name}/{chip.Device}/{channel}",
                    Chip: chip.Name,
                    Label: label,
                    Rpm: (int)rpm,
                    Name: HwmonCatalog.NameFan(label, IndexOf(channel))));
            }
        }

        return new HwmonSample([.. ResolveDuplicates(temps), ], [.. fans], withheld);
    }

    /// <summary>
    /// Point each relayed CPU channel at the reading it restates.
    /// </summary>
    /// <remarks>
    /// <para>A super-I/O <c>TSI</c>/<c>PECI</c>/<c>SMBUSMASTER</c> channel reads back the control
    /// temperature the CPU publishes about itself, which the CPU's own driver already reports directly.
    /// That makes them one measurement on two paths, and it is knowable from what the channels are —
    /// never from their values agreeing, which would fold two genuinely separate sensors the moment they
    /// happened to match and hide the day they diverged.</para>
    /// <para>Attributed only when the host has exactly one CPU package channel. A board with two relays
    /// and two sockets maps one relay to each, and nothing in hwmon says which — so with any other count
    /// the link is left unstated rather than guessed.</para>
    /// </remarks>
    private static List<SensorReading> ResolveDuplicates(List<SensorReading> temps)
    {
        string? packageId = null;
        int packages = 0;
        foreach (SensorReading r in temps)
        {
            if (!HwmonCatalog.IsCpuPackage(r.Chip, r.Label)) continue;
            packages++;
            packageId = r.Id;
        }

        if (packages != 1) return temps;

        for (int i = 0; i < temps.Count; i++)
        {
            if (HwmonCatalog.IsCpuRelay(temps[i].Chip, temps[i].Label))
                temps[i] = temps[i] with { DuplicateOf = packageId };
        }

        return temps;
    }

    /// <summary>
    /// "temp3" + "crit" -> the value of <c>temp3_crit</c> in °C, or null when the file is absent or
    /// unreadable. Kept to two decimals rather than the one a reading gets: a limit is a device SETTING,
    /// not a measurement at 0.1 °C granularity, and NVMe's are stored in Kelvin so they land on fractions
    /// — rounding a drive's 80.85 °C warning to 80.8 moves the line the alert fires on.
    /// </summary>
    private static double? ReadMilliC(string chipDir, string channel, string suffix) =>
        TryReadLong(Path.Combine(chipDir, $"{channel}_{suffix}"), out long milli)
            ? Math.Round(milli / 1000.0, 2)
            : null;

    /// <summary>
    /// The chip's stable identity: the basename its <c>device</c> symlink resolves to — a PCI address
    /// (<c>0000:00:18.3</c>), an i2c address (<c>0-0018</c>), a platform device. Falls back to the hwmon
    /// directory name when there is no such link, which is the best available for a purely virtual chip.
    /// </summary>
    private static string DeviceKey(string chipDir)
    {
        string link = Path.Combine(chipDir, "device");
        try
        {
            if (Directory.Exists(link) || File.Exists(link))
            {
                string? resolved = Path.GetFileName(Path.TrimEndingDirectorySeparator(
                    Directory.ResolveLinkTarget(link, returnFinalTarget: true)?.FullName ?? link));
                if (!string.IsNullOrEmpty(resolved)) return resolved;
            }
        }
        catch
        {
            // A dangling or unreadable link is not a failure to sample — fall through to the directory name.
        }

        return Path.GetFileName(chipDir);
    }

    // Deterministic order within a chip, so temp10 never sorts before temp2 by accident of enumeration.
    private static IEnumerable<string> Sorted(string dir, string glob)
    {
        var files = new List<string>(Directory.EnumerateFiles(dir, glob));
        files.Sort(static (a, b) => IndexOf(Channel(a)).CompareTo(IndexOf(Channel(b))));
        return files;
    }

    // "…/temp3_input" -> "temp3"
    private static string Channel(string inputPath)
    {
        string file = Path.GetFileName(inputPath);
        return file[..^"_input".Length];
    }

    // "temp3" -> 3, "fan12" -> 12. Zero when there are no trailing digits, which no real channel lacks.
    private static int IndexOf(string channel)
    {
        int i = channel.Length;
        while (i > 0 && char.IsAsciiDigit(channel[i - 1])) i--;
        return i < channel.Length && int.TryParse(channel.AsSpan(i), out int n) ? n : 0;
    }

    private static bool TryReadLong(string path, out long value)
    {
        value = 0;
        string? raw = ReadTrimmed(path);
        return raw is not null
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string? ReadTrimmed(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct ChipDir(string Dir, string Name, string Device);
}

/// <summary>
/// One hwmon walk: the temperatures, the fans that are turning, and how many super-I/O channels were
/// withheld as unconnected pins. The count exists so a dropped channel is an auditable number rather than
/// an unexplained gap between what <c>sensors</c> prints and what the panel shows.
/// </summary>
public readonly record struct HwmonSample(SensorReading[] Temps, FanReading[] Fans, int WithheldChannels);

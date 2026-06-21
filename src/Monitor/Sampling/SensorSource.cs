using System.Globalization;
using TheKrystalShip.KGSM.Monitor.Contracts;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Hardware temperatures from <c>/sys/class/hwmon/hwmon*/</c>. Each chip exposes a
/// <c>name</c> and one or more <c>tempN_input</c> files in milli-°C, with an optional
/// <c>tempN_label</c>. Instantaneous (no delta). The temp indices are <em>sparse</em>
/// (e.g. k10temp has <c>temp1</c> + <c>temp3</c>, no <c>temp2</c>), so we glob
/// <c>temp*_input</c> rather than loop a fixed range. The hwmon root is injectable so the
/// walk is golden-file testable against a synthetic tree.
/// </summary>
/// <remarks>
/// Honesty: when no hwmon chip exposes a temperature (or the tree is absent) the result is
/// an empty array — never an invented row. An unreadable file is skipped, not defaulted.
/// </remarks>
public sealed class SensorSource(string hwmonRoot = "/sys/class/hwmon")
{
    private readonly string _root = hwmonRoot;

    public SensorReading[] Sample() => Read(_root);

    /// <summary>Walk a hwmon root directory into temperature readings — pure over the filesystem.</summary>
    internal static SensorReading[] Read(string root)
    {
        if (!Directory.Exists(root))
            return [];

        var readings = new List<SensorReading>();

        foreach (var chipDir in Directory.GetDirectories(root))
        {
            string chip = ReadTrimmed(Path.Combine(chipDir, "name")) ?? Path.GetFileName(chipDir);

            // tempN_input files are sparse — enumerate them, don't assume a contiguous range.
            foreach (var inputPath in Directory.EnumerateFiles(chipDir, "temp*_input"))
            {
                string? raw = ReadTrimmed(inputPath);
                if (raw is null ||
                    !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long milliC))
                    continue; // unreadable / non-numeric -> skip, never fabricate

                // temp<N>_input -> temp<N>_label (the matching label, when present).
                string fileName = Path.GetFileName(inputPath);
                string labelName = fileName[..^"_input".Length] + "_label";
                string? label = ReadTrimmed(Path.Combine(chipDir, labelName));

                readings.Add(new SensorReading(chip, label, Math.Round(milliC / 1000.0, 1)));
            }
        }

        return [.. readings];
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
}

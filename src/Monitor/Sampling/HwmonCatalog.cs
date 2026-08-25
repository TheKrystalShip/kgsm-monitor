namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// What a hwmon channel actually measures, and what to call it. hwmon names channels after the register
/// that produced them (<c>Tctl</c>, <c>AUXTIN1</c>, <c>Composite</c>), which identifies the silicon rather
/// than the thing being measured; this turns that into a coarse role and a human name.
/// </summary>
/// <remarks>
/// <para><b>The knowledge lives here, once.</b> Every C# surface reads temperatures through the monitor,
/// so a chip table kept in the API or the SPA would have to be duplicated into the bot and the assistant
/// next. The daemon that reads the register is the thing that knows what the register is.</para>
/// <para><b>An unrecognised chip is reported, not dropped.</b> It comes through with a null role and a null
/// name, and a surface falls back to chip/label — the same raw pair it had before. A host whose hardware is
/// not in this table therefore loses nothing; it only gains no nicer name.</para>
/// <para><b>Super-I/O is the one class that gets filtered.</b> See <see cref="IsReal"/>.</para>
/// </remarks>
internal static class HwmonCatalog
{
    // Roles. Strings rather than an enum for the same reason ConditionReading.Metric is a string: the set
    // grows as hardware does, and a consumer pinned to an older contract must be able to receive a role it
    // has never heard of and still render the reading.
    internal const string RoleCpu = "cpu";
    internal const string RoleGpu = "gpu";
    internal const string RoleMemory = "memory";
    internal const string RoleDrive = "drive";
    internal const string RoleBoard = "board";
    internal const string RoleChipset = "chipset";
    internal const string RoleNetwork = "network";

    /// <summary>
    /// Super-I/O families (Nuvoton/ITE/Fintek/Winbond). These sit on the LPC bus and publish every
    /// thermistor input and every fan header the chip has pins for, whether or not the board wired one.
    /// No other hwmon class does this — a PCI or i2c chip exposes the sensors it physically has.
    /// </summary>
    internal static bool IsSuperIo(string chip) =>
        chip.StartsWith("nct", StringComparison.OrdinalIgnoreCase)
        || chip.StartsWith("it87", StringComparison.OrdinalIgnoreCase)
        || chip.StartsWith("it86", StringComparison.OrdinalIgnoreCase)
        || chip.StartsWith("f71", StringComparison.OrdinalIgnoreCase)
        || chip.StartsWith("w83", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a channel is measuring something, as opposed to reporting an unconnected pin.
    /// </summary>
    /// <remarks>
    /// <para>Only super-I/O chips are judged; everything else passes. An unwired thermistor input has no way
    /// to say "nothing here", so it reads a rail extreme instead — the driver's −128 °C open-circuit
    /// sentinel, or a pegged value up around 113 °C. Both are indistinguishable from a reading, which is
    /// what puts a 113 °C host on a dashboard and opens a danger-band alert that can never clear.</para>
    /// <para>The discriminator is physics: a super-I/O thermistor is soldered to the board and measures
    /// ambient, socket and VRM points, all of which live between 0 and 100 °C. Silicon sensors legitimately
    /// exceed that and are never range-judged — neither the dedicated chips (k10temp, coretemp, nvme, GPU)
    /// nor the super-I/O channels that merely <em>relay</em> silicon (see <see cref="RelaysSilicon"/>).</para>
    /// <para>The residual risk is a board that wires an AUXTIN to a VRM which genuinely passes 100 °C under
    /// load; that reading is dropped. A permanently-pegged pin is certain and a 100 °C VRM is hypothetical,
    /// so the trade runs this way.</para>
    /// </remarks>
    internal static bool IsReal(string chip, string? label, double valueC)
    {
        if (!IsSuperIo(chip)) return true;

        // PCH_* are Intel platform-controller registers. The driver publishes them on every platform; on
        // anything that is not an Intel PCH they read a hard zero. A real one never reads exactly 0.000.
        if (label is not null && label.StartsWith("PCH_", StringComparison.Ordinal))
            return valueC != 0.0;

        if (RelaysSilicon(label)) return true;

        return valueC > 0.0 && valueC < 100.0;
    }

    /// <summary>
    /// Super-I/O channels that carry a reading taken by another chip rather than by a board thermistor.
    /// The CPU publishes its own control temperature over SB-TSI/PECI and the super-I/O reads it back, so
    /// the value is silicon and may exceed the thermistor band under load.
    /// </summary>
    private static bool RelaysSilicon(string? label) =>
        label is not null
        && (label.StartsWith("TSI", StringComparison.Ordinal)
            || label.StartsWith("SMBUSMASTER", StringComparison.Ordinal)
            || label.StartsWith("PECI", StringComparison.Ordinal));

    /// <summary>
    /// Role and human name for a temperature channel. <paramref name="ordinal"/> is this chip's 1-based
    /// position among the chips sharing its name, and <paramref name="total"/> how many there are — which
    /// is what separates "Memory module 1" and "Memory module 2" from a lone "Memory module". Both null
    /// when the chip is not in the table.
    /// </summary>
    /// <remarks>
    /// A name states the thing being measured and not the quantity — "CPU", never "CPU temperature". The
    /// record already carries °C, so the quantity is the type's, and repeating it reads as noise wherever
    /// the unit is on screen anyway. A caller wanting a sentence composes one.
    /// </remarks>
    internal static (string? Role, string? Name) Classify(string chip, string? label, int ordinal, int total)
    {
        string c = chip.ToLowerInvariant();

        // AMD CPU. Tctl is the fan-control temperature (die max plus the model's offset); Tdie is the same
        // reading with the offset removed. Tccd<N> is the physical sensor on compute die N.
        if (c is "k10temp" or "zenpower" or "zenpower3")
        {
            if (label is null) return (RoleCpu, "CPU");
            if (label is "Tctl" or "Tdie") return (RoleCpu, "CPU");
            if (label.StartsWith("Tccd", StringComparison.Ordinal))
                return (RoleCpu, $"CPU die {label[4..]}");
            return (RoleCpu, $"CPU {label}");
        }

        // Intel CPU.
        if (c is "coretemp")
        {
            if (label is null) return (RoleCpu, "CPU");
            if (label.StartsWith("Package id ", StringComparison.Ordinal))
                return (RoleCpu, total > 1 || ordinal > 1
                    ? $"CPU package {label["Package id ".Length..]}"
                    : "CPU");
            if (label.StartsWith("Core ", StringComparison.Ordinal))
                return (RoleCpu, $"CPU core {label["Core ".Length..]}");
            return (RoleCpu, $"CPU {label}");
        }

        if (c is "amdgpu" or "nouveau" or "radeon")
        {
            string what = Ordinalise("GPU", ordinal, total);
            return label switch
            {
                "edge" => (RoleGpu, what),
                "junction" => (RoleGpu, $"{what} junction"),
                "mem" => (RoleGpu, $"{what} memory"),
                null => (RoleGpu, what),
                _ => (RoleGpu, $"{what} {label}"),
            };
        }

        // NVMe. Composite is the drive's headline figure; the numbered sensors are its individual probes.
        if (c is "nvme")
        {
            string what = Ordinalise("SSD", ordinal, total);
            if (label is null or "Composite") return (RoleDrive, what);
            if (label.StartsWith("Sensor ", StringComparison.Ordinal))
                return (RoleDrive, $"{what} sensor {label["Sensor ".Length..]}");
            return (RoleDrive, $"{what} {label}");
        }

        // SATA/SAS drives through the ATA SMART attribute.
        if (c is "drivetemp")
            return (RoleDrive, Ordinalise("Drive", ordinal, total));

        // DDR3/DDR4 DIMM thermal sensors on the SPD bus — one chip per populated module.
        if (c is "jc42" or "spd5118")
            return (RoleMemory, Ordinalise("Memory module", ordinal, total));

        if (c.StartsWith("iwlwifi", StringComparison.Ordinal) || c.StartsWith("mt79", StringComparison.Ordinal)
            || c.StartsWith("ath1", StringComparison.Ordinal) || c.StartsWith("athancient", StringComparison.Ordinal))
            return (RoleNetwork, "Wi-Fi adapter");

        if (IsSuperIo(c))
        {
            if (label is null) return (RoleBoard, $"Motherboard sensor {ordinal}");
            if (label is "CPUTIN") return (RoleBoard, "CPU socket");
            if (label is "SYSTIN") return (RoleBoard, "System");
            if (label.StartsWith("AUXTIN", StringComparison.Ordinal))
                return (RoleBoard, $"Auxiliary sensor {label["AUXTIN".Length..]}");
            // Several registers relay the same CPU reading, so the path has to be in the name or the panel
            // shows two identical rows and reads as a duplicate rather than as two channels.
            if (RelaysSilicon(label)) return (RoleCpu, $"CPU via {RelayPath(label)}");
            if (label.StartsWith("PCH_", StringComparison.Ordinal)) return (RoleChipset, "Chipset");
            return (RoleBoard, $"Motherboard {label}");
        }

        return (null, null);
    }

    /// <summary>How a relayed CPU reading names the register it came in on: <c>TSI0_TEMP</c> reads as
    /// <c>TSI0</c>, <c>SMBUSMASTER 0</c> as <c>SMBus 0</c>. The board publishes the same temperature under
    /// more than one of these, and the qualifier is the only thing that tells two such rows apart.</summary>
    private static string RelayPath(string label)
    {
        string trimmed = label.EndsWith("_TEMP", StringComparison.Ordinal) ? label[..^"_TEMP".Length] : label;
        return trimmed.StartsWith("SMBUSMASTER", StringComparison.Ordinal)
            ? "SMBus" + trimmed["SMBUSMASTER".Length..]
            : trimmed;
    }

    /// <summary>
    /// A human name for a fan tachometer.
    /// </summary>
    /// <remarks>
    /// Which physical fan a header drives is board wiring, and hwmon does not carry it: a super-I/O chip
    /// numbers its tachometer pins and stops there. Where a driver does publish a <c>fanN_label</c> that is
    /// the board's own answer and it wins; otherwise the channel is named by its number. Calling
    /// <c>fan1</c> "CPU fan" would be a guess at the wiring, and the guess is wrong on plenty of boards.
    /// </remarks>
    internal static string NameFan(string? label, int index) =>
        string.IsNullOrWhiteSpace(label) ? $"Fan {index}" : label!;

    // "SSD" alone when there is one, "SSD 2" when there are several — an index nobody needs is noise.
    private static string Ordinalise(string noun, int ordinal, int total) =>
        total > 1 ? $"{noun} {ordinal}" : noun;
}

using Microsoft.Extensions.Configuration;

namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Configuration binding and normalization. These build a configuration in memory rather than
/// mutating the process environment, so they say nothing about ambient state and run in parallel.
/// The environment's role — overriding a settings-file key — is covered by
/// <see cref="Environment_overrides_a_settings_file_key"/>, which is the one case that genuinely
/// needs a real environment provider.
/// </summary>
public class OptionsTests
{
    private static MonitorOptions Bind(params (string Key, string Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>($"{MonitorSettings.Section}:{v.Key}", v.Value)))
            .Build();

        return MonitorOptions.FromSettings(
            config.GetSection(MonitorSettings.Section).Get<MonitorSettings>() ?? new MonitorSettings());
    }

    // A knob written blank is "unset", not a startup crash and not zero. Both failure modes are real
    // binder behaviour on a non-nullable property: a blank value throws, and a null one binds to
    // 0/false — so `Monitor__IntervalMs=` left in an env file would have taken the daemon down, and a
    // JSON null would have set a 0ms sampling cadence nobody asked for. Every number and flag here is
    // nullable so both land on the coded default instead.
    [Fact]
    public void A_blank_value_means_unset_and_takes_the_coded_default()
    {
        var o = Bind(
            (nameof(MonitorSettings.IntervalMs), ""),
            (nameof(MonitorSettings.ServerResyncMs), ""),
            (nameof(MonitorSettings.DiskUsageMs), ""),
            (nameof(MonitorSettings.PersistMs), ""),
            (nameof(MonitorSettings.RawRetentionHours), ""),
            (nameof(MonitorSettings.RollupStepMin), ""),
            (nameof(MonitorSettings.RollupRetentionDays), ""),
            (nameof(MonitorSettings.MaintenanceMs), ""),
            (nameof(MonitorSettings.EventRetentionDays), ""),
            (nameof(MonitorSettings.EventsEnabled), ""),
            (nameof(MonitorSettings.HistoryDisabled), ""),
            (nameof(MonitorSettings.EventHistoryDisabled), ""));

        var defaults = new MonitorOptions();
        Assert.Equal(defaults.IntervalMs, o.IntervalMs);
        Assert.Equal(defaults.ServerResyncMs, o.ServerResyncMs);
        Assert.Equal(defaults.DiskUsageMs, o.DiskUsageMs);
        Assert.Equal(defaults.PersistMs, o.PersistMs);
        Assert.Equal(defaults.RawRetentionHours, o.RawRetentionHours);
        Assert.Equal(defaults.RollupStepMin, o.RollupStepMin);
        Assert.Equal(defaults.RollupRetentionDays, o.RollupRetentionDays);
        Assert.Equal(defaults.MaintenanceMs, o.MaintenanceMs);
        Assert.Equal(defaults.EventRetentionDays, o.EventRetentionDays);
        Assert.Equal(defaults.EventsEnabled, o.EventsEnabled);
        Assert.Equal(defaults.HistoryEnabled, o.HistoryEnabled);
        Assert.Equal(defaults.EventHistoryEnabled, o.EventHistoryEnabled);
    }

    // The other half of the contract: a value that is present but is not a number is NOT quietly
    // ignored. Typed configuration is worth having only if it refuses what it cannot read.
    [Fact]
    public void A_value_that_is_not_a_number_is_refused()
    {
        Assert.ThrowsAny<Exception>(() => Bind((nameof(MonitorSettings.IntervalMs), "soon")));
    }

    [Fact]
    public void Defaults_apply_when_nothing_is_configured()
    {
        var o = Bind();

        Assert.Equal(1000, o.IntervalMs);
        Assert.Equal("/run/kgsm-monitor/metrics.sock", o.SocketPath);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            o.SocketMode);
        Assert.Empty(o.MountFsDeny);
        Assert.Equal(["veth"], o.IfaceDenyPrefixes);
    }

    [Fact]
    public void Configured_values_are_bound()
    {
        var o = Bind(
            ("IntervalMs", "500"),
            ("SocketPath", "/tmp/x.sock"),
            ("SocketMode", "640"),
            ("MountFsDeny", "nfs, cifs"),
            ("IfaceDenyPrefixes", "veth, docker, br-"));

        Assert.Equal(500, o.IntervalMs);
        Assert.Equal("/tmp/x.sock", o.SocketPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, o.SocketMode); // 0640
        Assert.Contains("nfs", o.MountFsDeny);
        Assert.Contains("cifs", o.MountFsDeny);
        Assert.Equal(["veth", "docker", "br-"], o.IfaceDenyPrefixes);
    }

    [Fact]
    public void Sub_minimum_cadences_are_raised_to_their_floor()
    {
        // The floor is the nearest legal value to what was asked for. Reverting to the coded
        // default instead would silently run at a cadence nobody named.
        Assert.Equal(100, Bind(("IntervalMs", "50")).IntervalMs);
        Assert.Equal(1000, Bind(("PersistMs", "500")).PersistMs);
        Assert.Equal(1000, Bind(("ServerResyncMs", "10")).ServerResyncMs);
        Assert.Equal(5000, Bind(("DiskUsageMs", "1")).DiskUsageMs);
        Assert.Equal(1000, Bind(("MaintenanceMs", "0")).MaintenanceMs);
        Assert.Equal(1, Bind(("RawRetentionHours", "0")).RawRetentionHours);
        Assert.Equal(1, Bind(("RollupStepMin", "0")).RollupStepMin);
        Assert.Equal(1, Bind(("RollupRetentionDays", "0")).RollupRetentionDays);
        Assert.Equal(1, Bind(("EventRetentionDays", "0")).EventRetentionDays);
    }

    [Fact]
    public void History_defaults_apply_when_nothing_is_configured()
    {
        var o = Bind();

        Assert.True(o.HistoryEnabled);
        Assert.Equal("/var/lib/kgsm-monitor/metrics.db", o.HistoryDbPath);
        Assert.Equal(15_000, o.PersistMs);
        Assert.Equal(24, o.RawRetentionHours);
        Assert.Equal(5, o.RollupStepMin);
        Assert.Equal(30, o.RollupRetentionDays);
        Assert.Equal(60_000, o.MaintenanceMs);
        Assert.Equal(Environment.MachineName, o.HostId);
    }

    [Fact]
    public void History_values_are_bound()
    {
        var o = Bind(
            ("HistoryDisabled", "true"),
            ("HistoryDbPath", "/tmp/hist.db"),
            ("PersistMs", "30000"),
            ("RawRetentionHours", "48"),
            ("RollupStepMin", "10"),
            ("RollupRetentionDays", "90"),
            ("MaintenanceMs", "120000"),
            ("HostId", "hotrod"));

        Assert.False(o.HistoryEnabled);
        Assert.Equal("/tmp/hist.db", o.HistoryDbPath);
        Assert.Equal(30_000, o.PersistMs);
        Assert.Equal(48, o.RawRetentionHours);
        Assert.Equal(10, o.RollupStepMin);
        Assert.Equal(90, o.RollupRetentionDays);
        Assert.Equal(120_000, o.MaintenanceMs);
        Assert.Equal("hotrod", o.HostId);
    }

    [Fact]
    public void The_two_history_switches_are_independent()
    {
        Assert.True(Bind(("EventHistoryDisabled", "true")).HistoryEnabled);
        Assert.False(Bind(("EventHistoryDisabled", "true")).EventHistoryEnabled);
        Assert.True(Bind(("HistoryDisabled", "true")).EventHistoryEnabled);
    }

    [Fact]
    public void Malformed_socket_mode_keeps_the_default()
    {
        var o = Bind(("SocketMode", "not-octal"));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            o.SocketMode);
    }

    [Fact]
    public void Blank_values_fall_back_rather_than_configuring_an_empty_path()
    {
        // A settings file ships HostId as "" and it must still resolve to the machine name; the
        // same rule keeps a blank socket or database path from producing an unusable daemon.
        var o = Bind(("HostId", ""), ("SocketPath", ""), ("HistoryDbPath", "   "));

        Assert.Equal(Environment.MachineName, o.HostId);
        Assert.Equal("/run/kgsm-monitor/metrics.sock", o.SocketPath);
        Assert.Equal("/var/lib/kgsm-monitor/metrics.db", o.HistoryDbPath);
    }

    [Fact]
    public void An_empty_kgsm_path_leaves_per_server_sampling_off()
    {
        Assert.False(Bind().KgsmEnabled);
        Assert.True(Bind(("KgsmPath", "/usr/local/bin/kgsm")).KgsmEnabled);
    }

    [Fact]
    public void Environment_overrides_a_settings_file_key()
    {
        // The whole override model in one assertion: the settings file declares the key, the
        // environment changes it. Sources resolve in order, so the environment provider must be
        // registered after the file — get that backwards and an override reads as applied while
        // changing nothing.
        const string Key = "Monitor__SocketPath";
        Environment.SetEnvironmentVariable(Key, "/tmp/from-env.sock");
        try
        {
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection([new KeyValuePair<string, string?>("Monitor:SocketPath", "/from-file.sock")])
                .AddEnvironmentVariables()
                .Build();

            var o = MonitorOptions.FromSettings(
                config.GetSection(MonitorSettings.Section).Get<MonitorSettings>()!);

            Assert.Equal("/tmp/from-env.sock", o.SocketPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Key, null);
        }
    }
}

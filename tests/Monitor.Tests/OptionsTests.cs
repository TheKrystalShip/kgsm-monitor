namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>
/// Env-var configuration. These methods mutate process environment, so they live in
/// one class (xunit runs methods within a class sequentially) and each restores state.
/// </summary>
public class OptionsTests
{
    private static readonly string[] Keys =
    [
        "KGSM_MONITOR_INTERVAL_MS", "KGSM_MONITOR_SOCKET", "KGSM_MONITOR_SOCKET_MODE",
        "KGSM_MONITOR_MOUNT_FS_DENY", "KGSM_MONITOR_IFACE_DENY",
        "KGSM_MONITOR_HOST_ID", "KGSM_MONITOR_HISTORY_DISABLED", "KGSM_MONITOR_DB_PATH",
        "KGSM_MONITOR_PERSIST_MS", "KGSM_MONITOR_RAW_RETENTION_HOURS", "KGSM_MONITOR_ROLLUP_STEP_MIN",
        "KGSM_MONITOR_ROLLUP_RETENTION_DAYS", "KGSM_MONITOR_MAINT_MS",
    ];

    private static void Clear()
    {
        foreach (var k in Keys)
            Environment.SetEnvironmentVariable(k, null);
    }

    [Fact]
    public void Defaults_apply_when_unset()
    {
        Clear();
        var o = MonitorOptions.FromEnvironment();

        Assert.Equal(1000, o.IntervalMs);
        Assert.Equal("/run/kgsm-monitor/metrics.sock", o.SocketPath);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
            o.SocketMode);
        Assert.Empty(o.MountFsDeny);
        Assert.Equal(["veth"], o.IfaceDenyPrefixes);
    }

    [Fact]
    public void Overrides_are_read_from_environment()
    {
        Clear();
        Environment.SetEnvironmentVariable("KGSM_MONITOR_INTERVAL_MS", "500");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_SOCKET", "/tmp/x.sock");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_SOCKET_MODE", "640");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_MOUNT_FS_DENY", "nfs, cifs");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_IFACE_DENY", "veth, docker, br-");
        try
        {
            var o = MonitorOptions.FromEnvironment();

            Assert.Equal(500, o.IntervalMs);
            Assert.Equal("/tmp/x.sock", o.SocketPath);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, o.SocketMode); // 0640
            Assert.Contains("nfs", o.MountFsDeny);
            Assert.Contains("cifs", o.MountFsDeny);
            Assert.Equal(["veth", "docker", "br-"], o.IfaceDenyPrefixes);
        }
        finally
        {
            Clear();
        }
    }

    [Fact]
    public void Sub_minimum_interval_is_ignored()
    {
        Clear();
        Environment.SetEnvironmentVariable("KGSM_MONITOR_INTERVAL_MS", "50"); // below 100ms floor
        try
        {
            Assert.Equal(1000, MonitorOptions.FromEnvironment().IntervalMs);
        }
        finally
        {
            Clear();
        }
    }

    [Fact]
    public void History_defaults_apply_when_unset()
    {
        Clear();
        var o = MonitorOptions.FromEnvironment();

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
    public void History_overrides_are_read_from_environment()
    {
        Clear();
        Environment.SetEnvironmentVariable("KGSM_MONITOR_HISTORY_DISABLED", "1");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_DB_PATH", "/tmp/hist.db");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_PERSIST_MS", "30000");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_RAW_RETENTION_HOURS", "48");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_ROLLUP_STEP_MIN", "10");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_ROLLUP_RETENTION_DAYS", "90");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_MAINT_MS", "120000");
        Environment.SetEnvironmentVariable("KGSM_MONITOR_HOST_ID", "hotrod");
        try
        {
            var o = MonitorOptions.FromEnvironment();
            Assert.False(o.HistoryEnabled);
            Assert.Equal("/tmp/hist.db", o.HistoryDbPath);
            Assert.Equal(30_000, o.PersistMs);
            Assert.Equal(48, o.RawRetentionHours);
            Assert.Equal(10, o.RollupStepMin);
            Assert.Equal(90, o.RollupRetentionDays);
            Assert.Equal(120_000, o.MaintenanceMs);
            Assert.Equal("hotrod", o.HostId);
        }
        finally { Clear(); }
    }

    [Fact]
    public void Sub_minimum_persist_ms_is_ignored()
    {
        Clear();
        Environment.SetEnvironmentVariable("KGSM_MONITOR_PERSIST_MS", "500"); // below 1000ms floor
        try
        {
            Assert.Equal(15_000, MonitorOptions.FromEnvironment().PersistMs);
        }
        finally { Clear(); }
    }

    [Fact]
    public void Malformed_socket_mode_keeps_default()
    {
        Clear();
        Environment.SetEnvironmentVariable("KGSM_MONITOR_SOCKET_MODE", "not-octal");
        try
        {
            var o = MonitorOptions.FromEnvironment();
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite,
                o.SocketMode);
        }
        finally
        {
            Clear();
        }
    }
}

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

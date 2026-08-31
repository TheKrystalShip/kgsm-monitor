using TheKrystalShip.KGSM.ComponentConfig;

// What the Control Panel shows about this daemon, declared beside the configuration it describes.
// tools/LeafDescriptorGen reads this out of the built assembly and writes deploy/kgsm-monitor.leaf.json;
// deploy.sh installs that into /var/lib/kgsm/leaves/monitor.json, where kgsm-api scans for it. The
// daemon itself never reads any of this.

[assembly: Leaf(
    id: "monitor",
    displayName: "Monitor",
    unit: "kgsm-monitor.service",
    role: "Samples host and per-server resource metrics from the kernel, and owns metrics and engine-event history.")]

// Panel sections, in the order they render. Fields land in one by naming its id, and follow the order
// they are declared in MonitorSettings — so the page's layout is readable straight off the type.
[assembly: ConfigGroup("general", "General", 1)]
[assembly: ConfigGroup("sampling", "Sampling", 2)]
[assembly: ConfigGroup("sockets", "Sockets", 3)]
[assembly: ConfigGroup("servers", "Per-server metrics", 4)]
[assembly: ConfigGroup("leaves", "Per-leaf metrics", 5)]
[assembly: ConfigGroup("history", "Metrics history", 6)]
[assembly: ConfigGroup("events", "Event history", 7)]
[assembly: ConfigGroup("thresholds", "Thresholds", 8)]

// Where this daemon's own configuration comes from, lowest precedence first — the same order
// Program.cs resolves them in. The settings file is the base the other two override one key of.
[assembly: ConfigFloorSource("appsettings", "/opt/kgsm-monitor/kgsm-monitor.settings.json")]
[assembly: ConfigFloorSource("systemd-unit", "kgsm-monitor.service")]
[assembly: ConfigFloorSource("env-file", "/etc/kgsm-monitor/kgsm-monitor.env")]

// Per-category log filtering can name any category there is (Logging__LogLevel__Microsoft.AspNetCore
// and anything else a category name can spell), so the namespace cannot be enumerated. Every other
// key in the settings file has to be described or the build fails.
[assembly: ConfigFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

// The ecosystem logging level. It has no MonitorSettings property because
// Microsoft.Extensions.Logging owns it, so it is the one key nothing in this daemon's own types can
// be read to discover.
[assembly: ConfigFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = ConfigType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]

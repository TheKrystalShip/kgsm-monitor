using TheKrystalShip.KGSM.ComponentConfig;

// What a person may do with this daemon, through the node's API: the API checks them before it relays to
// the socket, so they are declared here, where they are performed, and checked there.
[assembly: Action("monitor:metrics.read", "See this machine's measurements and their history",
    DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action("monitor:thresholds.read", "See what this machine alerts on",
    DeclaredEffect.Read, DeclaredScope.Node)]
[assembly: Action("monitor:thresholds.write", "Change what this machine alerts on",
    DeclaredEffect.Write, DeclaredScope.Node)]

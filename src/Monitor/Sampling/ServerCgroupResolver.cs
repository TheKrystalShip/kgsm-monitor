using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Monitor.Sampling;

/// <summary>
/// Resolves a KGSM <see cref="Instance"/> to candidate cgroup v2 directories,
/// keyed on is-container. It never throws and never asserts liveness — it returns a
/// best-guess, ordered candidate set and lets <see cref="CgroupSampler"/> stat those
/// paths each tick, skipping a server whose cgroup is absent.
/// <para>
/// That single stat-and-skip collapses cases into one with zero extra process
/// spawns: a <em>stopped</em> container, an <em>unmatched container path</em>
/// (short-vs-full id, or the <c>cgroupfs</c> driver instead of <c>systemd</c>), and
/// a <em>native</em> server (no dedicated cgroup the resolver knows — deferred to
/// Slice 3's process-tree sampler).
/// </para>
/// </summary>
internal static class ServerCgroupResolver
{
    internal const string CgroupRoot = "/sys/fs/cgroup";

    /// <summary>Resolution kind plus ordered candidate cgroup directories (best guess first).</summary>
    internal readonly record struct Target(string Kind, IReadOnlyList<string> Candidates)
    {
        /// <summary>False when the server is not cgroup-addressable in Slice 2 (native standalone).</summary>
        public bool IsAddressable => Candidates.Count > 0;
    }

    /// <summary>
    /// Map an instance to its candidate cgroup directories. The returned
    /// <see cref="Target.Candidates"/> may point at paths that do not exist (the
    /// instance is stopped, or the container id/driver guess is wrong) — that is by
    /// design; existence is checked at sample time.
    /// </summary>
    internal static Target Resolve(Instance instance)
    {
        // container: the .pid file is overloaded to hold a Docker container id (a real
        // PID for native instances). compose_file is the verified is-container signal
        // (PLAN.md §6).
        if (!string.IsNullOrEmpty(instance.ComposeFile))
        {
            string id = ReadFirstLine(instance.PidFile);
            if (id.Length == 0)
                return new Target("container", []);

            return new Target("container",
            [
                Path.Combine(CgroupRoot, "system.slice", $"docker-{id}.scope"), // systemd cgroup driver (default)
                Path.Combine(CgroupRoot, "docker", id),                          // cgroupfs cgroup driver
            ]);
        }

        // native: no dedicated cgroup the resolver knows -> Slice 3 (ProcTreeSampler).
        return new Target("native", []);
    }

    /// <summary>First non-empty trimmed line of a file, or empty on any error/absence.</summary>
    internal static string ReadFirstLine(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return string.Empty;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    return trimmed;
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

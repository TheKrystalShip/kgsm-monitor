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
/// a <em>native</em> server whose cgroup has not been created yet.
/// <para>
/// A <em>native</em> server is cgroup-addressable when KGSM supplies its per-instance
/// cgroup directory via <see cref="Instance.CgroupPath"/> (<c>kgsm.slice/&lt;inst&gt;</c>,
/// the path kgsm-watchdog creates) — <see cref="CgroupSampler"/> then samples it directly.
/// A native server with an empty <see cref="Instance.CgroupPath"/> (cgroups disabled, or a
/// KGSM too old to emit the field) stays unaddressable and is deferred to Slice 3's
/// process-tree sampler. Its <see cref="Target.Kind"/> remains <c>"native"</c> in both cases.
/// </para>
/// </summary>
internal static class ServerCgroupResolver
{
    internal const string CgroupRoot = "/sys/fs/cgroup";

    /// <summary>Resolution kind plus ordered candidate cgroup directories (best guess first).</summary>
    internal readonly record struct Target(string Kind, IReadOnlyList<string> Candidates)
    {
        /// <summary>False when the server exposes no candidate cgroup directory to stat (a
        /// native server KGSM did not give a <c>cgroup_path</c>, or a container with no
        /// resolvable id) — such a server is covered by the process-tree fallback, not here.</summary>
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

        // native: KGSM derives the per-instance cgroup (kgsm.slice/<inst>) and surfaces it
        // as Instance.CgroupPath (Inc 4). When present, hand it to CgroupSampler as the
        // candidate; existence is still checked at sample time, so a not-yet-created cgroup
        // simply falls through. Empty CgroupPath (cgroups disabled, or a pre-Inc-4 KGSM) ->
        // no candidate -> ProcTreeSampler's /proc fallback owns it.
        if (!string.IsNullOrEmpty(instance.CgroupPath))
            return new Target("native", [instance.CgroupPath]);

        return new Target("native", []);
    }

    /// <summary>
    /// First candidate directory that currently exists, or <c>null</c> if none do. The single
    /// arbiter of "is this server's cgroup live", shared by <see cref="CgroupSampler"/> (which
    /// samples the live cgroup) and <see cref="ProcTreeSampler"/> (which cedes any native whose
    /// cgroup is live and falls back to <c>/proc</c> only for the rest) so the two samplers
    /// partition the watch-list with no overlap and no double-count.
    /// </summary>
    internal static string? FirstExisting(IReadOnlyList<string> candidates)
    {
        for (int i = 0; i < candidates.Count; i++)
            if (Directory.Exists(candidates[i]))
                return candidates[i];
        return null;
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

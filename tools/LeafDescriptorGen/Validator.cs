namespace TheKrystalShip.KGSM.LeafConfig.Gen;

/// <summary>
/// The structural rules of the descriptor format, checked at the point the file is produced.
/// </summary>
/// <remarks>
/// These ran as a per-repo unit test when the file was hand-written. Checking them here instead
/// moves them one step earlier — a descriptor that would fail them is never written at all — and
/// means one implementation covers every leaf rather than seven copies drifting apart.
/// </remarks>
internal static class Validator
{
    public static void Check(Descriptor descriptor, IEnumerable<string> settingsKeys)
    {
        var faults = new List<string>();

        CheckIdentity(descriptor, faults);
        CheckFloorSources(descriptor, faults);
        CheckFields(descriptor, faults);
        CheckCoverage(descriptor, settingsKeys, faults);

        if (faults.Count > 0)
            throw new GenException(
                $"the descriptor this leaf declares is not valid:\n  - {string.Join("\n  - ", faults)}");
    }

    // The descriptor's id must match the name it is installed under (/var/lib/kgsm/leaves/<id>.json),
    // not the name it is generated under — deploy-common.sh owns that rename and checks it there.
    private static void CheckIdentity(Descriptor descriptor, List<string> faults)
    {
        LeafIdentity identity = descriptor.Identity;

        if (!Names.ApplyModes.All.Contains(identity.ApplyMode))
            faults.Add($"applyMode '{identity.ApplyMode}' is not one of: {string.Join(", ", Names.ApplyModes.All)}");

        if (identity.ReadOnly && string.IsNullOrWhiteSpace(identity.ReadOnlyReason))
            faults.Add("readOnly is set with no readOnlyReason, so the panel would show a dead end with no explanation");

        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (GroupDef group in descriptor.Groups)
            if (!groupIds.Add(group.Id))
                faults.Add($"group '{group.Id}' is declared more than once");
    }

    private static void CheckFloorSources(Descriptor descriptor, List<string> faults)
    {
        IReadOnlyList<FloorSource> sources = descriptor.FloorSources;

        foreach (FloorSource source in sources)
            if (!Names.FloorKinds.All.Contains(source.Kind))
                faults.Add($"floor source kind '{source.Kind}' is not one of: {string.Join(", ", Names.FloorKinds.All)}");

        // The settings file is the base every other source overrides, and floorSources is read
        // lowest-precedence-first. Listed anywhere else, the panel resolves a knob to the file's value
        // and reports it as the deployed one — showing a blank where the unit sets a real path, on the
        // one screen whose job is saying where a value came from.
        if (sources.Count > 0 && sources[0].Kind != Names.FloorKinds.AppSettings)
            faults.Add(
                $"the first floor source is '{sources[0].Kind}', but the settings file is the lowest-precedence " +
                $"source and must be declared first");
    }

    private static void CheckFields(Descriptor descriptor, List<string> faults)
    {
        var groupIds = descriptor.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var keys = descriptor.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenEnvs = new HashSet<string>(StringComparer.Ordinal);

        foreach (FieldDef field in descriptor.Fields)
        {
            if (!seenKeys.Add(field.Key))
                faults.Add($"key '{field.Key}' is declared more than once; overrides are stored by key");

            if (!seenEnvs.Add(field.Env))
                faults.Add($"'{field.Env}' is described by more than one field");

            if (!Names.PanelTypes.All.Contains(field.Type))
                faults.Add($"{field.Key}: type '{field.Type}' is not one of: {string.Join(", ", Names.PanelTypes.All)}");

            if (!Names.Risks.All.Contains(field.Risk))
                faults.Add($"{field.Key}: risk '{field.Risk}' is not one of: {string.Join(", ", Names.Risks.All)}");

            if (field.Description.Length == 0)
                faults.Add(
                    $"{field.Key}: no description. Write a <panel> tag on the property saying what changing " +
                    $"it does, for whoever runs the host.");

            if (field.Group is not null && !groupIds.Contains(field.Group))
                faults.Add($"{field.Key}: group '{field.Group}' is not declared");

            if (field.DependsOn is not null && !keys.Contains(field.DependsOn))
                faults.Add($"{field.Key}: dependsOn '{field.DependsOn}' is not a field of this leaf");

            CheckEnum(field, faults);
            CheckBounds(field, faults);
        }
    }

    /// <summary>
    /// Every key the settings file declares is described, or explicitly exempt.
    /// </summary>
    /// <remarks>
    /// The settings file's leaves <i>are</i> the settable surface — a variable overrides a key only
    /// if that key exists there — so anything in it that no field describes is a knob the Control
    /// Panel cannot show or set. That is the drift this whole mechanism exists to remove, and it is
    /// the one direction the generator cannot fix by itself: it can derive a field's shape, but it
    /// cannot invent the label and prose for a knob nobody described.
    /// </remarks>
    private static void CheckCoverage(Descriptor descriptor, IEnumerable<string> settingsKeys, List<string> faults)
    {
        var described = descriptor.Fields.Select(f => f.Env).ToHashSet(StringComparer.Ordinal);

        List<string> undescribed = [.. settingsKeys
            .Where(k => !described.Contains(k))
            .Where(k => !descriptor.FrameworkNamespaces.Any(n => k.StartsWith(n.Prefix, StringComparison.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal)];

        foreach (string key in undescribed)
            faults.Add(
                $"'{key}' is declared in the settings file but no [LeafField] describes it, so the Control " +
                $"Panel cannot show or set it");

        // A described key the file does not declare binds to nothing: the panel would report the
        // override applied while the leaf carried on unchanged.
        var declared = settingsKeys.ToHashSet(StringComparer.Ordinal);

        foreach (FieldDef field in descriptor.Fields)
            if (!declared.Contains(field.Env) &&
                !descriptor.FrameworkNamespaces.Any(n => field.Env.StartsWith(n.Prefix, StringComparison.Ordinal)))
                faults.Add(
                    $"{field.Key} describes '{field.Env}', which the settings file does not declare — an " +
                    $"override of it would bind to nothing");
    }

    private static void CheckEnum(FieldDef field, List<string> faults)
    {
        if (field.Type != Names.PanelTypes.Enum)
        {
            if (field.Values is not null)
                faults.Add($"{field.Key}: values are declared on a '{field.Type}' field, where nothing reads them");

            return;
        }

        if (field.Values is null || field.Values.Count == 0)
        {
            faults.Add($"{field.Key}: an enum field with no values gives the panel nothing to offer");
            return;
        }

        if (field.Default is not null && !field.Values.Contains(field.Default))
            faults.Add($"{field.Key}: default '{field.Default}' is not one of its values");
    }

    private static void CheckBounds(FieldDef field, List<string> faults)
    {
        if (field.Min is int min && field.Max is int max && min > max)
            faults.Add($"{field.Key}: min {min} is above max {max}");

        // A bound is what the API rejects against before it restarts anything, so it has to be a bound
        // on something numeric.
        if ((field.Min is not null || field.Max is not null) &&
            field.Type is not (Names.PanelTypes.Int or Names.PanelTypes.Float or Names.PanelTypes.Duration))
            faults.Add($"{field.Key}: bounds are declared on a '{field.Type}' field, which has no numeric range");
    }
}

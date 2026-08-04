using System.Collections.Immutable;
using System.Reflection;

namespace TheKrystalShip.KGSM.LeafConfig.Gen;

/// <summary>
/// Reads a leaf's descriptor out of its compiled metadata.
/// </summary>
/// <remarks>
/// Everything here goes through <see cref="MetadataLoadContext"/> and
/// <see cref="CustomAttributeData"/>: types are inspected, never loaded for execution, so the tool
/// cannot run leaf code and never needs the leaf's runtime to be loadable in-process. Attribute
/// arguments are read from the metadata blob, which is why a bound like
/// <c>Min = IntervalMsFloor</c> resolves — the compiler has already folded the constant, so the
/// descriptor and the parser's clamp can share one declaration.
/// </remarks>
internal sealed class MetadataScanner(Assembly assembly, XmlDocs docs, IReadOnlyDictionary<string, string?> settings)
{
    private readonly List<string> warnings = [];

    public IReadOnlyList<string> Warnings => warnings;

    public Descriptor Scan()
    {
        LeafIdentity identity = ReadIdentity();
        List<GroupDef> groups = ReadGroups();
        List<FloorSource> floorSources = ReadFloorSources();

        var fields = new List<FieldDef>();
        fields.AddRange(ReadFrameworkFields());

        int order = fields.Count;
        foreach (Type type in assembly.GetTypes())
        {
            CustomAttributeData? section = Attr(type.GetCustomAttributesData(), Names.Attributes.Section);
            if (section is null)
                continue;

            ReadSection(type, Arg<string>(section, 0)!, fields, ref order);
        }

        return new Descriptor(identity, floorSources, groups, Sort(fields, groups), ReadFrameworkNamespaces());
    }

    private List<FrameworkNamespace> ReadFrameworkNamespaces() =>
        [.. assembly.GetCustomAttributesData()
            .Where(a => a.AttributeType.Name == Names.Attributes.FrameworkNamespace)
            .Select(a => new FrameworkNamespace(Arg<string>(a, 0)!, Arg<string>(a, 1)!))];

    // ── Leaf-level ───────────────────────────────────────────────────────────

    private LeafIdentity ReadIdentity()
    {
        CustomAttributeData leaf = Attr(assembly.GetCustomAttributesData(), Names.Attributes.Leaf)
            ?? throw new GenException(
                "the assembly carries no [assembly: Leaf(...)] attribute, so there is nothing to describe. " +
                "Declare it once, next to the leaf's settings type.");

        return new LeafIdentity(
            Id: Arg<string>(leaf, 0)!,
            DisplayName: Arg<string>(leaf, 1)!,
            Unit: Arg<string>(leaf, 2)!,
            Role: Arg<string>(leaf, 3)!,
            OnDemand: Named<bool>(leaf, Names.Args.OnDemand),
            ApplyMode: Named<string>(leaf, Names.Args.ApplyMode) ?? Names.ApplyModes.Restart,
            ReadOnly: Named<bool>(leaf, Names.Args.ReadOnly),
            ReadOnlyReason: Named<string>(leaf, Names.Args.ReadOnlyReason));
    }

    private List<GroupDef> ReadGroups() =>
        [.. assembly.GetCustomAttributesData()
            .Where(a => a.AttributeType.Name == Names.Attributes.Group)
            .Select(a => new GroupDef(Arg<string>(a, 0)!, Arg<string>(a, 1)!, Arg<int>(a, 2)))
            .OrderBy(g => g.Order)];

    private List<FloorSource> ReadFloorSources() =>
        [.. assembly.GetCustomAttributesData()
            .Where(a => a.AttributeType.Name == Names.Attributes.FloorSource)
            .Select(a => new FloorSource(Arg<string>(a, 0)!, Arg<string>(a, 1)!))];

    private List<FieldDef> ReadFrameworkFields()
    {
        var result = new List<FieldDef>();
        int order = 0;

        foreach (CustomAttributeData a in assembly.GetCustomAttributesData()
                     .Where(a => a.AttributeType.Name == Names.Attributes.FrameworkField))
        {
            string env = Arg<string>(a, 1)!;
            string description = Named<string>(a, Names.Args.Description) ?? string.Empty;

            result.Add(new FieldDef
            {
                Key = Arg<string>(a, 0)!,
                Env = env,
                Label = Arg<string>(a, 2)!,
                Description = description,
                Group = Named<string>(a, Names.Args.Group),
                Type = NamedEnum(a, Names.Args.Type) ?? Names.PanelTypes.String,
                Values = NamedArray(a, Names.Args.Values),
                // A framework key may or may not appear in the settings file. When it does the file is
                // the honest source; when it does not, the attribute is the only thing that can say.
                Default = settings.TryGetValue(env, out string? v) ? v : Named<string>(a, Names.Args.Default),
                Unit = Named<string>(a, Names.Args.Unit),
                Risk = NamedEnum(a, Names.Args.Risk) ?? Names.Risks.Safe,
                DescriptionFrom = description.Length > 0 ? DescriptionSource.Declared : DescriptionSource.Missing,
                Order = order++,
            });
        }

        return result;
    }

    // ── Bound properties ─────────────────────────────────────────────────────

    private void ReadSection(Type type, string envPrefix, List<FieldDef> into, ref int order)
    {
        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            ImmutableArray<CustomAttributeData> attrs = [.. prop.GetCustomAttributesData()];

            if (Attr(attrs, Names.Attributes.Ignore) is not null)
                continue;

            // A property the binder fills but no variable can deliver. One variable cannot express a
            // collection, and systemd refuses a variable name containing a hyphen, so a map keyed by
            // an instance name is undeliverable through the env file at all — it lives in the settings
            // file and stays off the panel. Skipping it here is what keeps that rule from depending on
            // anyone remembering it.
            if (IsCollection(prop.PropertyType))
                continue;

            string env = $"{envPrefix}{Names.EnvSeparator}{prop.Name}";
            CustomAttributeData? field = Attr(attrs, Names.Attributes.Field);

            if (field is null)
            {
                // A nested settings object: recurse, so Discord.Status.Online is addressed the way the
                // binder addresses it.
                if (IsBoundObject(prop.PropertyType))
                {
                    ReadSection(prop.PropertyType, env, into, ref order);
                    continue;
                }

                warnings.Add(
                    $"{type.Name}.{prop.Name} is bound configuration with no [LeafField] — it is settable " +
                    $"through {env} but invisible in the Control Panel.");
                continue;
            }

            into.Add(BuildField(type, prop, field, env, order++));
        }
    }

    private FieldDef BuildField(Type owner, PropertyInfo prop, CustomAttributeData attr, string env, int order)
    {
        (string description, DescriptionSource from) = docs.Describe(owner, prop);

        int min = Named(attr, Names.Args.Min, NoBound);
        int max = Named(attr, Names.Args.Max, NoBound);
        string declared = NamedEnum(attr, Names.Args.Type) ?? Names.PanelTypes.Auto;
        Type bare = Unwrap(prop.PropertyType);

        return new FieldDef
        {
            Key = Arg<string>(attr, 0)!,
            Env = env,
            Label = Arg<string>(attr, 1)!,
            Description = description,
            Group = Named<string>(attr, Names.Args.Group),
            Type = declared == Names.PanelTypes.Auto ? Derive(bare) : declared,
            Values = NamedArray(attr, Names.Args.Values) ?? EnumValues(bare),
            Default = Named<bool>(attr, Names.Args.NoDefault) ? null
                : settings.TryGetValue(env, out string? v) ? v : null,
            Min = min == NoBound ? null : min,
            Max = max == NoBound ? null : max,
            Unit = Named<string>(attr, Names.Args.Unit),
            Risk = NamedEnum(attr, Names.Args.Risk) ?? Names.Risks.Safe,
            PairedApiKey = Named<string>(attr, Names.Args.PairedApiKey),
            DependsOn = Named<string>(attr, Names.Args.DependsOn),
            DescriptionFrom = from,
            Order = order,
        };
    }

    // ── Type mapping ─────────────────────────────────────────────────────────

    /// <summary>Mirrors <c>LeafFieldAttribute.NoBound</c>: the value meaning "no bound declared".</summary>
    private const int NoBound = int.MinValue;

    private static Type Unwrap(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition().FullName == Names.Clr.Nullable
            ? t.GetGenericArguments()[0]
            : t;

    private static string Derive(Type t) => t.FullName switch
    {
        Names.Clr.String => Names.PanelTypes.String,
        Names.Clr.Boolean => Names.PanelTypes.Bool,
        Names.Clr.Int32 or Names.Clr.Int64 => Names.PanelTypes.Int,
        Names.Clr.Double or Names.Clr.Single or Names.Clr.Decimal => Names.PanelTypes.Float,
        _ when t.IsEnum => Names.PanelTypes.Enum,
        _ => throw new GenException(
            $"cannot derive a panel type for {t.FullName}. Say it explicitly with [LeafField(Type = ...)]."),
    };

    private static IReadOnlyList<string>? EnumValues(Type t) =>
        t.IsEnum ? [.. t.GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name)] : null;

    private static bool IsCollection(Type t) =>
        t.FullName != Names.Clr.String &&
        (t.IsArray ||
         (t.IsGenericType && Names.Clr.Collections.Contains(t.GetGenericTypeDefinition().FullName)));

    private static bool IsBoundObject(Type t) =>
        t is { IsClass: true, IsAbstract: false } &&
        t.FullName?.StartsWith(Names.Clr.SystemPrefix, StringComparison.Ordinal) != true;

    // ── Field ordering ───────────────────────────────────────────────────────

    /// <summary>
    /// Groups render in their declared order, and fields render in the order they are declared within
    /// each group — so the panel's layout is readable straight off the settings type. Anything
    /// pointing at no group sorts last, under <i>General</i>.
    /// </summary>
    private static List<FieldDef> Sort(List<FieldDef> fields, List<GroupDef> groups)
    {
        Dictionary<string, int> rank = groups
            .Select((g, i) => (g.Id, i))
            .ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

        return [.. fields
            .OrderBy(f => f.Group is not null && rank.TryGetValue(f.Group, out int r) ? r : int.MaxValue)
            .ThenBy(f => f.Order)];
    }

    // ── Attribute plumbing ───────────────────────────────────────────────────

    private static CustomAttributeData? Attr(IEnumerable<CustomAttributeData> attrs, string name) =>
        attrs.FirstOrDefault(a => a.AttributeType.Name == name);

    private static T? Arg<T>(CustomAttributeData a, int index) =>
        a.ConstructorArguments.Count > index ? (T?)a.ConstructorArguments[index].Value : default;

    private static T? Named<T>(CustomAttributeData a, string name)
    {
        foreach (CustomAttributeNamedArgument arg in a.NamedArguments)
            if (arg.MemberName == name)
                return (T?)arg.TypedValue.Value;

        return default;
    }

    private static T Named<T>(CustomAttributeData a, string name, T fallback)
    {
        foreach (CustomAttributeNamedArgument arg in a.NamedArguments)
            if (arg.MemberName == name)
                return (T)arg.TypedValue.Value!;

        return fallback;
    }

    /// <summary>
    /// An enum argument arrives as its underlying integer, so the member name is resolved from the
    /// enum's own metadata — the leaf's declaration is the authority, and this tool holds no mirror of
    /// it that could fall out of step.
    /// </summary>
    private static string? NamedEnum(CustomAttributeData a, string name)
    {
        foreach (CustomAttributeNamedArgument arg in a.NamedArguments)
        {
            if (arg.MemberName != name)
                continue;

            Type enumType = arg.TypedValue.ArgumentType;
            object value = arg.TypedValue.Value!;

            foreach (FieldInfo member in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
                if (Equals(member.GetRawConstantValue(), value))
                    return member.Name.ToLowerInvariant();

            throw new GenException($"{enumType.Name} has no member with value {value} (argument '{name}').");
        }

        return null;
    }

    private static IReadOnlyList<string>? NamedArray(CustomAttributeData a, string name)
    {
        foreach (CustomAttributeNamedArgument arg in a.NamedArguments)
        {
            if (arg.MemberName != name)
                continue;

            if (arg.TypedValue.Value is not IReadOnlyCollection<CustomAttributeTypedArgument> items)
                return null;

            return [.. items.Select(i => (string)i.Value!)];
        }

        return null;
    }
}

/// <summary>A fault in what the leaf declared. Reported by message, never by stack trace.</summary>
internal sealed class GenException(string message) : Exception(message);

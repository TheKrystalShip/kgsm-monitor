// The leaf config descriptor, declared where the configuration itself is declared.
//
// These attributes are compiled into the leaf assembly and read back out of its metadata by
// tools/LeafDescriptorGen, which writes deploy/<leaf>.leaf.json. Nothing here is read at runtime:
// the leaf never reflects over itself, so an AOT leaf carries these as inert metadata and ILC
// discards them. That is why this is a shared *source* file rather than a package reference —
// there is no assembly to trim, and the generator matches attributes by full type name, so each
// leaf compiling its own copy costs nothing.
//
// Format and rules: tks/leaf-config-descriptor.md.

namespace TheKrystalShip.KGSM.LeafConfig;

/// <summary>
/// Identifies the assembly as a KGSM leaf and carries the descriptor's leaf-level keys.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class LeafAttribute(string id, string displayName, string unit, string role) : Attribute
{
    /// <summary>Stable leaf id, lowercase kebab. Becomes the descriptor filename stem.</summary>
    public string Id { get; } = id;

    /// <summary>Human name for the panel.</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>The systemd unit the API restarts to apply a change.</summary>
    public string Unit { get; } = unit;

    /// <summary>One sentence: what this leaf does.</summary>
    public string Role { get; } = role;

    /// <summary>True for a leaf that idle-exits, so the panel does not read "inactive" as a fault.</summary>
    public bool OnDemand { get; set; }

    /// <summary><c>restart</c> or <c>reload</c>.</summary>
    public string ApplyMode { get; set; } = "restart";

    /// <summary>True for a leaf whose configuration is readable but not editable from the panel.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Why, in the leaf's own words. Required when <see cref="ReadOnly"/>.</summary>
    public string? ReadOnlyReason { get; set; }
}

/// <summary>
/// One section of the panel. Fields land in a group by naming its id; the order given here is the
/// order the groups render in, and it is also the order fields are emitted in.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class LeafGroupAttribute(string id, string label, int order) : Attribute
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public int Order { get; } = order;
}

/// <summary>
/// Where this leaf's own configuration comes from, <b>lowest precedence first</b> — the same order
/// the leaf itself resolves them, so the settings file is declared first.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class LeafFloorSourceAttribute(string kind, string path) : Attribute
{
    /// <summary><c>appsettings</c>, <c>systemd-unit</c> or <c>env-file</c>.</summary>
    public string Kind { get; } = kind;

    public string Path { get; } = path;
}

/// <summary>
/// Marks a bound settings type and names the configuration section it binds from. Every
/// <see cref="LeafFieldAttribute"/> property under it is addressed as <c>Section__Property</c>,
/// which is exactly how <c>IConfiguration</c> maps an environment variable onto it — so a
/// descriptor cannot name a variable the leaf does not read.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class LeafSectionAttribute(string section) : Attribute
{
    public string Section { get; } = section;
}

/// <summary>The wire type the panel renders a field as. <c>Auto</c> derives it from the property.</summary>
internal enum LeafType
{
    Auto,
    String,
    Int,
    Bool,
    Enum,
    Secret,
    Path,
    Csv,
    Duration,
    Float,
}

/// <summary>How the panel presents an edit. Never blocks one.</summary>
internal enum LeafRisk
{
    /// <summary>The failure mode is the leaf doing its job differently.</summary>
    Safe,

    /// <summary>Changing it can sever the link between this leaf and something else.</summary>
    Wiring,

    /// <summary>Changing it can drop data.</summary>
    Destructive,
}

/// <summary>
/// Declares one configurable knob. The <c>env</c> name and the coded default are derived — the
/// first from the property's position in the section, the second from the settings file — so this
/// carries only what cannot be derived: the stable key, the operator-facing label, and the
/// presentation.
/// </summary>
/// <remarks>
/// The field's <c>description</c> comes from a <c>&lt;panel&gt;</c> tag in the property's XML doc
/// comment, falling back to <c>&lt;summary&gt;</c>. Operator prose and developer prose answer
/// different questions, which is why they get separate tags rather than one shared sentence.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class LeafFieldAttribute(string key, string label) : Attribute
{
    /// <summary>No bound value. <see cref="Min"/>/<see cref="Max"/> default to this and are omitted.</summary>
    public const int NoBound = int.MinValue;

    /// <summary>
    /// The stable wire id, camelCase. <b>Immutable once shipped</b> — stored overrides are keyed by
    /// it, so renaming one orphans a live override and silently reverts the leaf to its floor. It is
    /// spelled out rather than derived from the property name for exactly that reason: a property
    /// rename must not be able to move it.
    /// </summary>
    public string Key { get; } = key;

    /// <summary>Short human name. No units here; use <see cref="Unit"/>.</summary>
    public string Label { get; } = label;

    /// <summary>A <see cref="LeafGroupAttribute"/> id. Unset renders under <i>General</i>.</summary>
    public string? Group { get; set; }

    /// <summary>Wire type. <see cref="LeafType.Auto"/> reads it off the property's CLR type.</summary>
    public LeafType Type { get; set; } = LeafType.Auto;

    /// <summary>Allowed values for an enum field. Derived automatically from a C# enum property.</summary>
    public string[]? Values { get; set; }

    /// <summary>Lower bound. Mirror the parser's own floor by pointing both at one constant.</summary>
    public int Min { get; set; } = NoBound;

    /// <summary>Upper bound.</summary>
    public int Max { get; set; } = NoBound;

    /// <summary>Display suffix: <c>ms</c>, <c>s</c>, <c>days</c>, <c>MB</c>, <c>%</c>.</summary>
    public string? Unit { get; set; }

    public LeafRisk Risk { get; set; } = LeafRisk.Safe;

    /// <summary>A kgsm-api config key that must move in lockstep with this one.</summary>
    public string? PairedApiKey { get; set; }

    /// <summary>Another field's key. This one has no effect unless that one is set.</summary>
    public string? DependsOn { get; set; }

    /// <summary>
    /// Suppresses the derived default. Set it when the settings file's value is not what the leaf
    /// actually falls back to — a blank that resolves to the machine name at runtime is not a
    /// default of empty string, and publishing it as one would be a fabricated value.
    /// </summary>
    public bool NoDefault { get; set; }
}

/// <summary>
/// Excludes a bound property from the descriptor. Collections and name-keyed maps are skipped
/// automatically (one variable cannot express a collection, and systemd refuses a variable name
/// containing a hyphen, so a map keyed by an instance name is undeliverable through the env file);
/// this is for the rest.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class LeafIgnoreAttribute : Attribute;

/// <summary>
/// A namespace of configuration keys this leaf honours but does not describe, because the set is
/// open-ended by construction — per-category log filtering can spell any category name there is.
/// </summary>
/// <remarks>
/// Every other key in the settings file must be described or the build fails. This is the one
/// escape hatch, and it is deliberately a declaration rather than a rule buried in a tool: an
/// exemption that has to be written down, with a reason, is one someone has to justify.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class LeafFrameworkNamespaceAttribute(string prefix, string reason) : Attribute
{
    /// <summary>Key prefix, spelled the way the settings file flattens: <c>Logging__</c>.</summary>
    public string Prefix { get; } = prefix;

    /// <summary>Why this namespace cannot be enumerated.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// A configurable key the leaf honours without binding it to a settings property — the ecosystem
/// logging level, a host-builder variable. It is declared explicitly, with its own default, because
/// nothing in the leaf's own types can be read to discover it.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
internal sealed class LeafFrameworkFieldAttribute(string key, string env, string label) : Attribute
{
    public string Key { get; } = key;
    public string Env { get; } = env;
    public string Label { get; } = label;

    /// <summary>Operator-facing prose. No property means no XML doc to fall back on.</summary>
    public string Description { get; set; } = string.Empty;

    public string? Group { get; set; }
    public LeafType Type { get; set; } = LeafType.String;
    public string[]? Values { get; set; }
    public string? Default { get; set; }
    public string? Unit { get; set; }
    public LeafRisk Risk { get; set; } = LeafRisk.Safe;
}

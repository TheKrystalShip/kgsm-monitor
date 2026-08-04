namespace TheKrystalShip.KGSM.LeafConfig.Gen;

/// <summary>
/// Every name the tool matches on, in one place. The scanner reads metadata rather than loading the
/// leaf's types, so each of these is a name resolved at runtime rather than by the compiler —
/// spelling one wrong would silently drop a field rather than fail to build, which is exactly the
/// class of drift this tool exists to remove.
/// </summary>
internal static class Names
{
    /// <summary>Attribute type names, matched unqualified so a leaf may namespace them as it likes.</summary>
    internal static class Attributes
    {
        internal const string Leaf = "LeafAttribute";
        internal const string Group = "LeafGroupAttribute";
        internal const string FloorSource = "LeafFloorSourceAttribute";
        internal const string Section = "LeafSectionAttribute";
        internal const string Field = "LeafFieldAttribute";
        internal const string Ignore = "LeafIgnoreAttribute";
        internal const string FrameworkField = "LeafFrameworkFieldAttribute";
        internal const string FrameworkNamespace = "LeafFrameworkNamespaceAttribute";
    }

    /// <summary>Named attribute arguments. These match the property names in LeafConfigAttributes.cs.</summary>
    internal static class Args
    {
        internal const string OnDemand = "OnDemand";
        internal const string ApplyMode = "ApplyMode";
        internal const string ReadOnly = "ReadOnly";
        internal const string ReadOnlyReason = "ReadOnlyReason";
        internal const string Group = "Group";
        internal const string Type = "Type";
        internal const string Values = "Values";
        internal const string Min = "Min";
        internal const string Max = "Max";
        internal const string Unit = "Unit";
        internal const string Risk = "Risk";
        internal const string PairedApiKey = "PairedApiKey";
        internal const string DependsOn = "DependsOn";
        internal const string NoDefault = "NoDefault";
        internal const string Description = "Description";
        internal const string Default = "Default";
    }

    /// <summary>The descriptor's `type` vocabulary (tks/leaf-config-descriptor.md §Types).</summary>
    internal static class PanelTypes
    {
        /// <summary>Not a wire value — the marker meaning "read it off the property".</summary>
        internal const string Auto = "auto";

        internal const string String = "string";
        internal const string Int = "int";
        internal const string Bool = "bool";
        internal const string Enum = "enum";
        internal const string Secret = "secret";
        internal const string Path = "path";
        internal const string Csv = "csv";
        internal const string Duration = "duration";
        internal const string Float = "float";

        internal static readonly string[] All =
            [String, Int, Bool, Enum, Secret, Path, Csv, Duration, Float];
    }

    /// <summary>The descriptor's `risk` vocabulary.</summary>
    internal static class Risks
    {
        internal const string Safe = "safe";
        internal const string Wiring = "wiring";
        internal const string Destructive = "destructive";

        internal static readonly string[] All = [Safe, Wiring, Destructive];
    }

    /// <summary>The descriptor's `floorSources[].kind` vocabulary.</summary>
    internal static class FloorKinds
    {
        internal const string AppSettings = "appsettings";
        internal const string SystemdUnit = "systemd-unit";
        internal const string EnvFile = "env-file";

        internal static readonly string[] All = [AppSettings, SystemdUnit, EnvFile];
    }

    /// <summary>CLR type names the panel-type derivation recognises.</summary>
    internal static class Clr
    {
        internal const string String = "System.String";
        internal const string Boolean = "System.Boolean";
        internal const string Int32 = "System.Int32";
        internal const string Int64 = "System.Int64";
        internal const string Double = "System.Double";
        internal const string Single = "System.Single";
        internal const string Decimal = "System.Decimal";
        internal const string Nullable = "System.Nullable`1";

        /// <summary>Namespace prefix marking a type as the framework's rather than the leaf's.</summary>
        internal const string SystemPrefix = "System.";

        internal static readonly string[] Collections =
        [
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.Generic.IDictionary`2",
            "System.Collections.Generic.IReadOnlyDictionary`2",
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.IList`1",
            "System.Collections.Generic.IReadOnlyList`1",
            "System.Collections.Generic.HashSet`1",
        ];
    }

    /// <summary>Descriptor JSON property names.</summary>
    internal static class Json
    {
        internal const string SchemaVersion = "schemaVersion";
        internal const string Id = "id";
        internal const string DisplayName = "displayName";
        internal const string Unit = "unit";
        internal const string Role = "role";
        internal const string OnDemand = "onDemand";
        internal const string ApplyMode = "applyMode";
        internal const string ReadOnly = "readOnly";
        internal const string ReadOnlyReason = "readOnlyReason";
        internal const string FloorSources = "floorSources";
        internal const string Kind = "kind";
        internal const string Path = "path";
        internal const string Groups = "groups";
        internal const string Label = "label";
        internal const string Order = "order";
        internal const string Fields = "fields";
        internal const string Key = "key";
        internal const string Env = "env";
        internal const string Description = "description";
        internal const string Group = "group";
        internal const string Type = "type";
        internal const string Values = "values";
        internal const string Default = "default";
        internal const string Min = "min";
        internal const string Max = "max";
        internal const string Risk = "risk";
        internal const string PairedApiKey = "pairedApiKey";
        internal const string DependsOn = "dependsOn";
    }

    /// <summary>XML documentation-file names.</summary>
    internal static class Docs
    {
        /// <summary>Operator-facing prose: what changing this knob does, for someone running the host.</summary>
        internal const string PanelTag = "panel";

        /// <summary>Developer-facing prose, the fallback when no panel tag was written.</summary>
        internal const string SummaryTag = "summary";

        /// <summary>The doc-file id prefix for a property member.</summary>
        internal const string PropertyPrefix = "P:";

        internal const string MembersElement = "members";
        internal const string MemberElement = "member";
        internal const string NameAttribute = "name";

        /// <summary>Attributes carrying an empty element's content: &lt;see cref="…"/&gt;.</summary>
        internal const string CrefAttribute = "cref";
        internal const string LangwordAttribute = "langword";

        internal const string Extension = ".xml";
    }

    /// <summary>The `applyMode` vocabulary.</summary>
    internal static class ApplyModes
    {
        internal const string Restart = "restart";
        internal const string Reload = "reload";

        internal static readonly string[] All = [Restart, Reload];
    }

    /// <summary>The env-var path separator IConfiguration maps onto a section boundary.</summary>
    internal const string EnvSeparator = "__";

    /// <summary>The descriptor schema this tool emits.</summary>
    internal const int SchemaVersion = 1;
}

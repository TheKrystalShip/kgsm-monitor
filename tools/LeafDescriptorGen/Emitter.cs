using System.Text.Encodings.Web;
using System.Text.Json;

namespace TheKrystalShip.KGSM.LeafConfig.Gen;

/// <summary>
/// Writes the descriptor JSON. Key order is fixed rather than incidental so the file diffs cleanly:
/// a descriptor is reviewed in a pull request, and a reordering commit that changes nothing would
/// bury the one line that does.
/// </summary>
internal static class Emitter
{
    private static readonly JsonWriterOptions Options = new()
    {
        Indented = true,

        // Field descriptions are prose: apostrophes, em dashes, arrows. The default encoder escapes
        // those to \uXXXX for HTML-injection safety, which this file does not need — it is parsed as
        // JSON and rendered by a framework that escapes on output, never interpolated into markup. The
        // relaxed encoder still escapes everything JSON itself requires; what it buys is a descriptor
        // that reads as written in a diff, which is where these files are reviewed.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Render(Descriptor descriptor)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            writer.WriteStartObject();

            WriteIdentity(writer, descriptor.Identity);
            WriteFloorSources(writer, descriptor.FloorSources);
            WriteGroups(writer, descriptor.Groups);
            WriteFields(writer, descriptor.Fields);

            writer.WriteEndObject();
        }

        // A text file ends with a newline; Utf8JsonWriter does not write one.
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    private static void WriteIdentity(Utf8JsonWriter writer, LeafIdentity identity)
    {
        writer.WriteNumber(Names.Json.SchemaVersion, Names.SchemaVersion);
        writer.WriteString(Names.Json.Id, identity.Id);
        writer.WriteString(Names.Json.DisplayName, identity.DisplayName);
        writer.WriteString(Names.Json.Unit, identity.Unit);
        writer.WriteString(Names.Json.Role, identity.Role);
        writer.WriteBoolean(Names.Json.OnDemand, identity.OnDemand);
        writer.WriteString(Names.Json.ApplyMode, identity.ApplyMode);

        // Omitted rather than written false: the format's readers treat absence as "not read-only",
        // and every leaf but one is exactly that.
        if (identity.ReadOnly)
        {
            writer.WriteBoolean(Names.Json.ReadOnly, true);
            WriteOptional(writer, Names.Json.ReadOnlyReason, identity.ReadOnlyReason);
        }
    }

    private static void WriteFloorSources(Utf8JsonWriter writer, IReadOnlyList<FloorSource> sources)
    {
        writer.WriteStartArray(Names.Json.FloorSources);

        foreach (FloorSource source in sources)
        {
            writer.WriteStartObject();
            writer.WriteString(Names.Json.Kind, source.Kind);
            writer.WriteString(Names.Json.Path, source.Path);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteGroups(Utf8JsonWriter writer, IReadOnlyList<GroupDef> groups)
    {
        if (groups.Count == 0)
            return;

        writer.WriteStartArray(Names.Json.Groups);

        foreach (GroupDef group in groups)
        {
            writer.WriteStartObject();
            writer.WriteString(Names.Json.Id, group.Id);
            writer.WriteString(Names.Json.Label, group.Label);
            writer.WriteNumber(Names.Json.Order, group.Order);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteFields(Utf8JsonWriter writer, IReadOnlyList<FieldDef> fields)
    {
        writer.WriteStartArray(Names.Json.Fields);

        foreach (FieldDef field in fields)
        {
            writer.WriteStartObject();

            writer.WriteString(Names.Json.Key, field.Key);
            writer.WriteString(Names.Json.Env, field.Env);
            writer.WriteString(Names.Json.Label, field.Label);
            writer.WriteString(Names.Json.Description, field.Description);
            WriteOptional(writer, Names.Json.Group, field.Group);
            writer.WriteString(Names.Json.Type, field.Type);

            if (field.Values is not null)
            {
                writer.WriteStartArray(Names.Json.Values);
                foreach (string value in field.Values)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            WriteOptional(writer, Names.Json.Default, field.Default);

            if (field.Min is int min)
                writer.WriteNumber(Names.Json.Min, min);

            if (field.Max is int max)
                writer.WriteNumber(Names.Json.Max, max);

            WriteOptional(writer, Names.Json.Unit, field.Unit);
            writer.WriteString(Names.Json.Risk, field.Risk);
            WriteOptional(writer, Names.Json.PairedApiKey, field.PairedApiKey);
            WriteOptional(writer, Names.Json.DependsOn, field.DependsOn);

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes a key only when it has a value. Null is absence, and the format's readers distinguish
    /// an absent <c>default</c> ("this leaf has none") from a present empty one ("its default is
    /// blank") — writing null for both would collapse that distinction.
    /// </summary>
    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
            writer.WriteString(name, value);
    }
}

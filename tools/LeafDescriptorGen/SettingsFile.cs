using System.Text.Json;

namespace TheKrystalShip.KGSM.LeafConfig.Gen;

/// <summary>
/// The leaf's settings file, flattened the way <c>IConfiguration</c> maps environment variables:
/// <c>Section__Key</c>. This is where every field's coded default comes from — the same artifact the
/// leaf loads at runtime, so a default in the descriptor cannot claim a value the leaf does not
/// actually start with.
/// </summary>
internal static class SettingsFile
{
    public static IReadOnlyDictionary<string, string?> Flatten(string path)
    {
        if (!File.Exists(path))
            throw new GenException($"the settings file is missing: {path}");

        // Comments and trailing commas, because Microsoft.Extensions.Configuration's own JSON provider
        // accepts them and these files carry explanatory headers.
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        var flat = new Dictionary<string, string?>(StringComparer.Ordinal);
        Walk(doc.RootElement, prefix: string.Empty, flat);
        return flat;
    }

    private static void Walk(JsonElement node, string prefix, Dictionary<string, string?> into)
    {
        foreach (JsonProperty prop in node.EnumerateObject())
        {
            string key = prefix.Length == 0 ? prop.Name : prefix + Names.EnvSeparator + prop.Name;

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                Walk(prop.Value, key, into);
                continue;
            }

            into[key] = Scalar(prop.Value);
        }
    }

    /// <summary>
    /// The descriptor carries every default as a string, because that is what an environment variable
    /// can deliver. A JSON <c>null</c> stays null and the field simply has no default — the leaf
    /// genuinely has nothing to fall back to, and inventing an empty string would be a fabricated
    /// value.
    /// </summary>
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };
}

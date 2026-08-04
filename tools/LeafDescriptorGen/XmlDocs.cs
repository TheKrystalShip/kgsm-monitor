using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TheKrystalShip.KGSM.LeafConfig.Gen;

/// <summary>
/// The operator-facing prose for each field, read from the compiler's XML documentation file.
/// </summary>
/// <remarks>
/// A field's description comes from a <c>&lt;panel&gt;</c> tag on the property, falling back to
/// <c>&lt;summary&gt;</c>. The two are kept apart because they answer different questions: the
/// summary tells a developer what the value means to the code (<i>"floor 100 — a lower value is
/// raised to it"</i>), while the panel tells whoever runs the host what changing it does. The
/// fallback exists so a new knob is never shipped <i>describing nothing</i>; the tool reports every
/// use of it, so it stays a stopgap rather than a habit.
/// </remarks>
internal sealed partial class XmlDocs
{
    private readonly Dictionary<string, XElement> members = new(StringComparer.Ordinal);

    public bool Found { get; }

    public XmlDocs(string? path)
    {
        if (path is null || !File.Exists(path))
            return;

        Found = true;

        XElement? root = XDocument.Load(path).Root?.Element(Names.Docs.MembersElement);
        if (root is null)
            return;

        foreach (XElement member in root.Elements(Names.Docs.MemberElement))
        {
            string? name = member.Attribute(Names.Docs.NameAttribute)?.Value;
            if (name is not null)
                members[name] = member;
        }
    }

    public (string Text, DescriptionSource From) Describe(Type owner, PropertyInfo prop)
    {
        string id = Names.Docs.PropertyPrefix + $"{owner.FullName}.{prop.Name}".Replace('+', '.');

        if (!members.TryGetValue(id, out XElement? member))
            return (string.Empty, DescriptionSource.Missing);

        if (Text(member.Element(Names.Docs.PanelTag)) is { Length: > 0 } panel)
            return (panel, DescriptionSource.Panel);

        if (Text(member.Element(Names.Docs.SummaryTag)) is { Length: > 0 } summary)
            return (summary, DescriptionSource.Summary);

        return (string.Empty, DescriptionSource.Missing);
    }

    /// <summary>
    /// Flattens a doc element to plain prose. Inline markup carries no meaning on the panel — a
    /// <c>&lt;see cref="Foo"/&gt;</c> renders as the name it points at, because the operator reading
    /// it has no code to navigate to.
    /// </summary>
    private static string? Text(XElement? element)
    {
        if (element is null)
            return null;

        var buffer = new StringBuilder();

        foreach (XNode node in element.DescendantNodes())
        {
            switch (node)
            {
                case XText text:
                    buffer.Append(text.Value);
                    break;

                // An empty element carries its content in the attribute: <see cref="T:Ns.Type"/>.
                case XElement { IsEmpty: true } reference:
                    string? target =
                        reference.Attribute(Names.Docs.CrefAttribute)?.Value ??
                        reference.Attribute(Names.Docs.LangwordAttribute)?.Value;
                    if (target is not null)
                        buffer.Append(target.Contains(':') ? target[(target.IndexOf(':') + 1)..] : target);
                    break;
            }
        }

        return Whitespace().Replace(buffer.ToString(), " ").Trim() is { Length: > 0 } prose ? prose : null;
    }

    /// <summary>Doc comments wrap across lines; the panel wants one paragraph.</summary>
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

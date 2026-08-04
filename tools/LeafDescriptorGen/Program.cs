using System.Reflection;
using System.Runtime.InteropServices;

namespace TheKrystalShip.KGSM.LeafConfig.Gen;

/// <summary>
/// Writes a leaf's config descriptor from the leaf's own compiled metadata.
/// </summary>
/// <remarks>
/// The descriptor is what the Control Panel renders a leaf's configuration page from, and it used to
/// be written by hand beside a settings class it had no mechanical connection to. Deriving it means
/// the two cannot disagree: the environment variable comes from the property's position in its
/// bound section, and the default from the settings file the leaf actually loads.
/// <para>
/// This runs as its own process against the built assembly, and reads it as metadata only. Nothing
/// is loaded for execution, so describing a leaf costs it no reflection and no dependency — which is
/// what keeps the Native-AOT leaves AOT.
/// </para>
/// </remarks>
internal static class Program
{
    private const string ArgAssembly = "--assembly";
    private const string ArgSettings = "--settings";
    private const string ArgOut = "--out";
    private const string ArgCheck = "--check";

    private const int Ok = 0;
    private const int Failed = 1;

    private const string Usage = $"""
        usage: leafdescgen {ArgAssembly} <leaf.dll> {ArgSettings} <kgsm-<leaf>.settings.json> {ArgOut} <leaf.json> [{ArgCheck}]

          {ArgAssembly}  the leaf's built assembly, read as metadata (never loaded for execution)
          {ArgSettings}  the leaf's settings file, which supplies each field's coded default
          {ArgOut}       where the descriptor is written
          {ArgCheck}     write nothing; fail if the file on disk is not what this would write
        """;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (GenException ex)
        {
            Console.Error.WriteLine($"leafdescgen: {ex.Message}");
            return Failed;
        }
    }

    private static int Run(string[] args)
    {
        string? assemblyPath = Value(args, ArgAssembly);
        string? settingsPath = Value(args, ArgSettings);
        string? outPath = Value(args, ArgOut);
        bool check = args.Contains(ArgCheck);

        if (assemblyPath is null || settingsPath is null || outPath is null)
        {
            Console.Error.WriteLine(Usage);
            return Failed;
        }

        using MetadataLoadContext context = Open(assemblyPath);
        Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));

        var docs = new XmlDocs(Path.ChangeExtension(assemblyPath, Names.Docs.Extension));
        if (!docs.Found)
            Console.Error.WriteLine(
                "leafdescgen: no XML documentation file beside the assembly — every field will need its " +
                "description spelled out. Set <GenerateDocumentationFile>true</GenerateDocumentationFile>.");

        IReadOnlyDictionary<string, string?> settings = SettingsFile.Flatten(settingsPath);
        var scanner = new MetadataScanner(assembly, docs, settings);
        Descriptor descriptor = scanner.Scan();

        Validator.Check(descriptor, settings.Keys);

        foreach (string warning in scanner.Warnings)
            Console.Error.WriteLine($"leafdescgen: {warning}");

        ReportFallbacks(descriptor);

        string rendered = Emitter.Render(descriptor);
        return check ? Compare(outPath, rendered) : Write(outPath, rendered, descriptor);
    }

    /// <summary>
    /// A description that fell back to the developer-facing summary still ships, but it is named — a
    /// silent fallback is how the panel ends up explaining a knob in terms of the code behind it.
    /// </summary>
    private static void ReportFallbacks(Descriptor descriptor)
    {
        foreach (FieldDef field in descriptor.Fields.Where(f => f.DescriptionFrom == DescriptionSource.Summary))
            Console.Error.WriteLine(
                $"leafdescgen: {field.Key} is described by its <summary>, which is written for a developer. " +
                $"Add a <panel> tag to say what changing it does for whoever runs the host.");
    }

    private static int Write(string path, string rendered, Descriptor descriptor)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);

        // Rewriting an identical file would touch its timestamp for nothing, and this runs on every build.
        if (File.Exists(path) && File.ReadAllText(path) == rendered)
        {
            Console.WriteLine($"leafdescgen: {descriptor.Identity.Id} unchanged ({descriptor.Fields.Count} fields)");
            return Ok;
        }

        File.WriteAllText(path, rendered);
        Console.WriteLine($"leafdescgen: wrote {path} ({descriptor.Fields.Count} fields)");
        return Ok;
    }

    private static int Compare(string path, string rendered)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"leafdescgen: {path} does not exist. Build the leaf to generate it.");
            return Failed;
        }

        string actual = File.ReadAllText(path);
        if (actual == rendered)
            return Ok;

        Console.Error.WriteLine(
            $"leafdescgen: {path} is not what this leaf declares.\n{FirstDifference(actual, rendered)}\n" +
            $"Rebuild to regenerate it, and commit the result.");

        return Failed;
    }

    /// <summary>Names the first line that differs, so a stale file says what changed rather than that it did.</summary>
    private static string FirstDifference(string actual, string expected)
    {
        string[] a = actual.Split('\n');
        string[] b = expected.Split('\n');

        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            string left = i < a.Length ? a[i] : "(end of file)";
            string right = i < b.Length ? b[i] : "(end of file)";

            if (left != right)
                return $"  line {i + 1}\n    on disk:  {left.Trim()}\n    declared: {right.Trim()}";
        }

        return "  the files differ only in trailing whitespace";
    }

    /// <summary>
    /// Resolves the leaf's assembly and everything it references from its own output directory plus
    /// the running framework — enough to read metadata, which is all this needs.
    /// </summary>
    private static MetadataLoadContext Open(string assemblyPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath));
        if (directory is null || !File.Exists(assemblyPath))
            throw new GenException($"the assembly is missing: {assemblyPath}. Build the leaf first.");

        List<string> assemblies =
        [
            .. Directory.GetFiles(directory, "*.dll"),
            .. Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"),
        ];

        return new MetadataLoadContext(new PathAssemblyResolver(assemblies));
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

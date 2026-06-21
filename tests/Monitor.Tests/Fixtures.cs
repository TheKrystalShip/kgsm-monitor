namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>Helper to read a captured <c>/proc</c> fixture from the test output dir.</summary>
internal static class Fixtures
{
    private static readonly string Dir =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Read(string name) => File.ReadAllText(System.IO.Path.Combine(Dir, name));

    /// <summary>Absolute path of a fixture file or subtree (for the injectable <c>/sys</c> + <c>/proc</c> roots).</summary>
    public static string Path(string relative) => System.IO.Path.Combine(Dir, relative);
}

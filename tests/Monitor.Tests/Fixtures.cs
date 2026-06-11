namespace TheKrystalShip.KGSM.Monitor.Tests;

/// <summary>Helper to read a captured <c>/proc</c> fixture from the test output dir.</summary>
internal static class Fixtures
{
    private static readonly string Dir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Read(string name) => File.ReadAllText(Path.Combine(Dir, name));
}

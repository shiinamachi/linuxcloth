namespace LinuxCloth.Catalog.Tests;

internal static class Fixture
{
    public static byte[] ReadBytes(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

using Avalonia.Headless;

namespace LinuxCloth.Desktop.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessUiTestGroup : ICollectionFixture<HeadlessUiFixture>
{
    public const string Name = "Headless UI";
}

public sealed class HeadlessUiFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(typeof(App));

    public void Dispose()
    {
        Session.Dispose();
    }
}

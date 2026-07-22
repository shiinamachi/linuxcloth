using System.Xml.Linq;

namespace LinuxCloth.Desktop.Tests;

public sealed class DesktopProjectPolicyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DevelopmentRunPublishesGuestBridgeNextToDesktop()
    {
        var path = Path.Combine(
            RepositoryRoot,
            "src/LinuxCloth.Desktop/LinuxCloth.Desktop.csproj");
        var document = XDocument.Load(path);
        var target = document
            .Descendants("Target")
            .Single(element => (string?)element.Attribute("Name") ==
                "PublishGuestBridgeForDevelopmentRun");

        Assert.Equal("Run", (string?)target.Attribute("BeforeTargets"));
        Assert.Equal("Publish", (string?)target.Element("MSBuild")?.Attribute("Targets"));
        var copyDestination = (string?)target.Element("Copy")?.Attribute("DestinationFiles");
        Assert.NotNull(copyDestination);
        Assert.Contains(
            "$(TargetDir)guest/linuxcloth-guest-bridge.exe",
            copyDestination,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("linuxcloth 저장소 루트를 찾지 못했습니다.");
    }
}

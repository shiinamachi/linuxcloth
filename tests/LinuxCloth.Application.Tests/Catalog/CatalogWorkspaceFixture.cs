using System.Text;
using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.Storage;

namespace LinuxCloth.Application.Tests.Catalog;

internal sealed class CatalogWorkspaceFixture : IDisposable
{
    private const string DefaultCommit = "1111111111111111111111111111111111111111";
    private static readonly byte[] Png =
    [
        137, 80, 78, 71, 13, 10, 26, 10,
        0, 0, 0, 13, 73, 72, 68, 82,
        0, 0, 0, 1, 0, 0, 0, 1,
        8, 6, 0, 0, 0, 0, 0, 0, 0,
    ];

    public CatalogWorkspaceFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"lc-catalog-workspace-{Guid.NewGuid():N}");
        Paths = new LinuxClothPaths(
            Path.Combine(Root, "config"),
            Path.Combine(Root, "data"),
            Path.Combine(Root, "cache"),
            Path.Combine(Root, "runtime"));
        Paths.CreateBaseDirectories();
    }

    public string Root { get; }

    public LinuxClothPaths Paths { get; }

    public OfficialCatalogBundle CreateBundle(
        string name,
        string displayName = "우리은행",
        string commit = DefaultCommit,
        bool duplicatePayInfo = false)
    {
        var root = Path.Combine(Root, "bundles", name);
        var docs = Path.Combine(root, "docs");
        var images = Path.Combine(docs, "images");
        Directory.CreateDirectory(Path.Combine(images, "Banking"));
        Directory.CreateDirectory(Path.Combine(images, "Government"));
        File.WriteAllBytes(Path.Combine(images, "Banking", "WooriBank.png"), Png);
        File.WriteAllBytes(Path.Combine(images, "Government", "Gov24.png"), Png);
        File.WriteAllText(
            Path.Combine(docs, "Catalog.xml"),
            CreateCatalog(displayName, duplicatePayInfo),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new OfficialCatalogBundle(
            Path.Combine(docs, "Catalog.xml"),
            images,
            OfficialCatalogBundle.OfficialRepository,
            commit);
    }

    public static string CreateCatalog(string displayName, bool duplicatePayInfo = false) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <TableClothCatalog Fallback="ko-KR">
          <InternetServices>
            <Service Id="WooriBank" DisplayName="{{displayName}}" Category="Banking" Url="https://www.wooribank.com/" en-US-DisplayName="Woori Bank">
              <SearchKeywords>우리;은행</SearchKeywords>
            </Service>
            <Service Id="Gov24" DisplayName="정부24" Category="Government" Url="https://www.gov.kr/" />
            {{(duplicatePayInfo ? "<Service Id=\"Gov24\" DisplayName=\"나중 중복\" Category=\"Other\" Url=\"https://example.test/\" />" : string.Empty)}}
          </InternetServices>
        </TableClothCatalog>
        """;

    public static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "vendor",
                    "TableClothCatalog",
                    "docs",
                    "Catalog.xml")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The linuxcloth repository root could not be found.");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

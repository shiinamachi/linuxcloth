namespace LinuxCloth.Catalog.Tests;

public sealed class CatalogIndexTests
{
    private readonly CatalogIndex _index = new(
        new CatalogParser().Parse(Fixture.ReadBytes("Catalog.xml")));

    [Fact]
    public void CategoriesAndCategoryResultsHaveStableOrdering()
    {
        Assert.Equal(
            [CatalogCategory.Banking, CatalogCategory.Government],
            _index.Categories);
        Assert.Equal(
            ["KookminBank", "WooriBank"],
            _index.GetByCategory(CatalogCategory.Banking).Select(service => service.Id.Value));
    }

    [Fact]
    public void SearchUsesIdentifiersNamesAndOfficialKeywords()
    {
        Assert.Equal("WooriBank", Assert.Single(_index.Search("woori")).Id.Value);
        Assert.Equal("Gov24", Assert.Single(_index.Search("민원")).Id.Value);
        Assert.Equal("KookminBank", Assert.Single(_index.Search("kookmin")).Id.Value);
    }

    [Fact]
    public void SearchIsDeterministicAndSupportsCategoryFiltering()
    {
        var first = _index.Search("bank").Select(service => service.Id.Value).ToArray();
        var second = _index.Search("BANK").Select(service => service.Id.Value).ToArray();

        Assert.Equal(["KookminBank", "WooriBank"], first);
        Assert.Equal(first, second);
        Assert.Empty(_index.Search("bank", CatalogCategory.Government));
    }
}

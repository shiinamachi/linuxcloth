namespace LinuxCloth.Catalog;

public sealed class CatalogIndex
{
    private static readonly StringComparer SortComparer = StringComparer.Ordinal;
    private readonly IReadOnlyList<CatalogService> _services;

    public CatalogIndex(CatalogDocument catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _services = Sort(catalog.Services).AsReadOnly();
        Categories = _services
            .Select(service => service.Category)
            .Distinct()
            .OrderBy(category => category.ToString(), SortComparer)
            .ToArray();
    }

    public IReadOnlyList<CatalogCategory> Categories { get; }

    public IReadOnlyList<CatalogService> All => _services;

    public IReadOnlyList<CatalogService> GetByCategory(CatalogCategory category) =>
        _services.Where(service => service.Category == category).ToArray();

    public IReadOnlyList<CatalogService> Search(
        string? query,
        CatalogCategory? category = null)
    {
        var tokens = (query ?? string.Empty).Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var candidates = category is null
            ? _services
            : _services.Where(service => service.Category == category).ToArray();

        if (tokens.Length == 0)
        {
            return candidates.ToArray();
        }

        return candidates
            .Select(service => new SearchResult(service, Score(service, tokens)))
            .Where(result => result.Score < int.MaxValue)
            .OrderBy(result => result.Score)
            .ThenBy(result => result.Service.DisplayName, SortComparer)
            .ThenBy(result => result.Service.Id.Value, SortComparer)
            .Select(result => result.Service)
            .ToArray();
    }

    private static List<CatalogService> Sort(IEnumerable<CatalogService> services) =>
        services
            .OrderBy(service => service.DisplayName, SortComparer)
            .ThenBy(service => service.Id.Value, SortComparer)
            .ToList();

    private static int Score(CatalogService service, IReadOnlyList<string> tokens)
    {
        var fields = new List<string>
        {
            service.Id.Value,
            service.DisplayName,
            service.Url.AbsoluteUri,
        };

        if (service.EnglishDisplayName is not null)
        {
            fields.Add(service.EnglishDisplayName);
        }

        fields.AddRange(service.SearchKeywords);

        var score = 0;
        foreach (var token in tokens)
        {
            var tokenScore = fields.Min(field => MatchScore(field, token));
            if (tokenScore == int.MaxValue)
            {
                return int.MaxValue;
            }

            score += tokenScore;
        }

        return score;
    }

    private static int MatchScore(string field, string token)
    {
        if (string.Equals(field, token, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (field.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return field.Contains(token, StringComparison.OrdinalIgnoreCase) ? 2 : int.MaxValue;
    }

    private sealed record SearchResult(CatalogService Service, int Score);
}

using Avalonia.Media.Imaging;
using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.Images;
using LinuxCloth.Catalog;
using LinuxCloth.Core;
using LinuxCloth.Desktop.Infrastructure;

namespace LinuxCloth.Desktop.ViewModels;

public sealed class ServiceCardViewModel : ObservableObject, IDisposable
{
    private readonly CatalogServiceEntry _entry;

    public ServiceCardViewModel(CatalogServiceEntry entry)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        if (entry.Image is not null)
        {
            try
            {
                Image = new Bitmap(entry.Image.Path);
            }
            catch (Exception exception) when (exception is IOException or ArgumentException)
            {
                Image = null;
            }
        }
    }

    public ServiceId Id => _entry.Service.Id;

    public string DisplayName => _entry.Service.DisplayName;

    public string EnglishDisplayName => _entry.Service.EnglishDisplayName ?? string.Empty;

    public string Category => CategoryName(_entry.Service.Category);

    public CatalogCategory CategoryValue => _entry.Service.Category;

    public string Url => _entry.Service.Url.GetLeftPart(UriPartial.Authority);

    public string SearchText => string.Join(
        ' ',
        new[] { DisplayName, EnglishDisplayName, Id.Value }
            .Concat(_entry.Service.SearchKeywords));

    public string CompatibilityLabel => _entry.Compatibility.Status switch
    {
        CompatibilityStatus.Verified => "Linux 검증됨",
        CompatibilityStatus.Partial => "부분 지원",
        CompatibilityStatus.Blocked => "현재 차단됨",
        _ => "미검증",
    };

    public bool IsBlocked => _entry.Compatibility.Status == CompatibilityStatus.Blocked;

    public string CompatibilityNotes => string.Join(
        Environment.NewLine,
        new[] { _entry.Service.CompatNotes }
            .Concat(_entry.Compatibility.KnownIssues)
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

    public string PackageSummary =>
        $"설치 패키지 {_entry.Service.Packages.Count}개 · Edge 확장 {_entry.Service.EdgeExtensions.Count}개";

    public bool HasCustomBootstrap => _entry.Service.HasCustomBootstrap;

    public Bitmap? Image { get; }

    public void Dispose()
    {
        Image?.Dispose();
        GC.SuppressFinalize(this);
    }

    public static string CategoryName(CatalogCategory category) => category switch
    {
        CatalogCategory.Banking => "은행",
        CatalogCategory.Financing => "금융",
        CatalogCategory.Security => "증권",
        CatalogCategory.Insurance => "보험",
        CatalogCategory.CreditCard => "카드",
        CatalogCategory.Government => "정부",
        CatalogCategory.Education => "교육",
        _ => "기타",
    };
}

public sealed record CategoryFilterViewModel(string Label, CatalogCategory? Value);

public sealed record ImageChoiceViewModel(ImageId Id, string Label);

public sealed record DoctorCheckViewModel(string Label, bool IsAvailable, bool IsRequired, string Detail);

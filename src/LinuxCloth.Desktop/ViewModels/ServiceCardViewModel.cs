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
        CompatibilityStatus.Verified => "이용 가능",
        CompatibilityStatus.Partial => "제한 있음",
        CompatibilityStatus.Blocked => "이용 불가",
        _ => "미확인",
    };

    public bool IsVerified => _entry.Compatibility.Status == CompatibilityStatus.Verified;

    public bool IsPartial => _entry.Compatibility.Status == CompatibilityStatus.Partial;

    public bool IsBlocked => _entry.Compatibility.Status == CompatibilityStatus.Blocked;

    public bool IsUnknown =>
        _entry.Compatibility.Status is not (
            CompatibilityStatus.Verified or
            CompatibilityStatus.Partial or
            CompatibilityStatus.Blocked);

    public string CompatibilityNotes => string.Join(
        Environment.NewLine,
        new[] { _entry.Service.CompatNotes }
            .Concat(_entry.Compatibility.KnownIssues)
            .Where(static value => !string.IsNullOrWhiteSpace(value)));

    public bool HasCompatibilityNotes => !string.IsNullOrWhiteSpace(CompatibilityNotes);

    public string CompatibilityDescription => string.IsNullOrWhiteSpace(CompatibilityNotes)
        ? _entry.Compatibility.Status switch
        {
            CompatibilityStatus.Verified => "이 환경에서 이용할 수 있습니다.",
            CompatibilityStatus.Partial => "일부 기능에 제한이 있을 수 있습니다.",
            CompatibilityStatus.Blocked => "현재 환경에서는 열 수 없습니다.",
            _ => "이 환경에서의 지원 상태가 아직 확인되지 않았습니다.",
        }
        : CompatibilityNotes;

    public string PackageSummary =>
        $"패키지 {_entry.Service.Packages.Count} · Edge 확장 {_entry.Service.EdgeExtensions.Count}";

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

public sealed record DoctorCheckViewModel(string Label, bool IsAvailable, bool IsRequired, string Detail)
{
    public string Status => IsAvailable ? "✓" : "!";

    public string StatusLabel => IsAvailable ? "사용 가능" : IsRequired ? "설정 필요" : "권장";
}

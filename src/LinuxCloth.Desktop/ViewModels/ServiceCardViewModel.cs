using Avalonia.Media.Imaging;
using LinuxCloth.Application.Catalog;
using LinuxCloth.Application.Images;
using LinuxCloth.Catalog;
using LinuxCloth.Core;
using LinuxCloth.Desktop.Infrastructure;
using LinuxCloth.Desktop.Localization;

namespace LinuxCloth.Desktop.ViewModels;

public sealed class ServiceCardViewModel : ObservableObject, IDisposable
{
    private readonly CatalogServiceEntry _entry;

    public ServiceCardViewModel(CatalogServiceEntry entry)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        UiStrings.Instance.CultureChanged += OnCultureChanged;
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

    public string DisplayName =>
        UiStrings.Instance.CurrentCulture.TwoLetterISOLanguageName == "en" &&
        !string.IsNullOrWhiteSpace(EnglishDisplayName)
            ? EnglishDisplayName
            : _entry.Service.DisplayName;

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
        CompatibilityStatus.Verified => Text("Catalog.Compatibility.Verified"),
        CompatibilityStatus.Partial => Text("Catalog.Compatibility.Partial"),
        CompatibilityStatus.Blocked => Text("Catalog.Compatibility.Blocked"),
        _ => Text("Catalog.Compatibility.Unknown"),
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
            CompatibilityStatus.Verified => Text("Catalog.Compatibility.VerifiedDescription"),
            CompatibilityStatus.Partial => Text("Catalog.Compatibility.PartialDescription"),
            CompatibilityStatus.Blocked => Text("Catalog.Compatibility.BlockedDescription"),
            _ => Text("Catalog.Compatibility.UnknownDescription"),
        }
        : CompatibilityNotes;

    public string PackageSummary => UiStrings.Instance.Format(
        "Catalog.PackageSummary",
        _entry.Service.Packages.Count,
        _entry.Service.EdgeExtensions.Count);

    public bool HasCustomBootstrap => _entry.Service.HasCustomBootstrap;

    public Bitmap? Image { get; }

    public void Dispose()
    {
        UiStrings.Instance.CultureChanged -= OnCultureChanged;
        Image?.Dispose();
        GC.SuppressFinalize(this);
    }

    public static string CategoryName(CatalogCategory category) => category switch
    {
        CatalogCategory.Banking => Text("Catalog.Category.Banking"),
        CatalogCategory.Financing => Text("Catalog.Category.Financing"),
        CatalogCategory.Security => Text("Catalog.Category.Security"),
        CatalogCategory.Insurance => Text("Catalog.Category.Insurance"),
        CatalogCategory.CreditCard => Text("Catalog.Category.CreditCard"),
        CatalogCategory.Government => Text("Catalog.Category.Government"),
        CatalogCategory.Education => Text("Catalog.Category.Education"),
        _ => Text("Catalog.Category.Other"),
    };

    private void OnCultureChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(CompatibilityLabel));
        OnPropertyChanged(nameof(CompatibilityDescription));
        OnPropertyChanged(nameof(PackageSummary));
    }

    private static string Text(string key) => UiStrings.Instance[key];
}

public sealed record CategoryFilterViewModel(string Label, CatalogCategory? Value);

public sealed record ImageChoiceViewModel(ImageId Id, string Label);

public sealed record DoctorCheckViewModel(string Label, bool IsAvailable, bool IsRequired, string Detail)
{
    public string Status => IsAvailable ? "✓" : "!";

    public string StatusLabel => IsAvailable
        ? UiStrings.Instance["Catalog.Check.Available"]
        : IsRequired
            ? UiStrings.Instance["Catalog.Check.Required"]
            : UiStrings.Instance["Catalog.Check.Recommended"];
}

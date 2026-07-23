using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace LinuxCloth.Desktop.Localization;

public sealed class UiStrings : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new(
        "LinuxCloth.Desktop.Localization.Strings",
        typeof(UiStrings).Assembly);

    private UiLanguageOption _selectedLanguage;

    private UiStrings()
    {
        Languages =
        [
            new UiLanguageOption("ko-KR", "한국어"),
            new UiLanguageOption("en-US", "English"),
        ];
        var preferredLanguage = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en"
            ? "en-US"
            : "ko-KR";
        _selectedLanguage = Languages.Single(language =>
            string.Equals(language.CultureName, preferredLanguage, StringComparison.Ordinal));
        ApplyCulture(_selectedLanguage);
    }

    public static UiStrings Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? CultureChanged;

    public IReadOnlyList<UiLanguageOption> Languages { get; }

    public UiLanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var supported = Languages.SingleOrDefault(language =>
                string.Equals(language.CultureName, value.CultureName, StringComparison.OrdinalIgnoreCase));
            if (supported is null || ReferenceEquals(_selectedLanguage, supported))
            {
                return;
            }

            _selectedLanguage = supported;
            ApplyCulture(supported);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLanguage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo(SelectedLanguage.CultureName);

    public string this[string key] =>
        Resources.GetString(key, CurrentCulture) ??
        throw new InvalidOperationException($"UI 문자열 리소스가 없습니다: {key}");

    public string GetOrDefault(string key, string fallback) =>
        Resources.GetString(key, CurrentCulture) ?? fallback;

    public string Format(string key, params object?[] arguments) =>
        string.Format(CurrentCulture, this[key], arguments);

    public void SelectCulture(string cultureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureName);
        SelectedLanguage = Languages.Single(language =>
            string.Equals(language.CultureName, cultureName, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyCulture(UiLanguageOption language)
    {
        var culture = CultureInfo.GetCultureInfo(language.CultureName);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}

public sealed record UiLanguageOption(string CultureName, string DisplayName);

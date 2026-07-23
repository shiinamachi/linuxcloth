using LinuxCloth.Desktop.Localization;

namespace LinuxCloth.Desktop.Tests;

public sealed class UiStringsTests
{
    [Fact]
    public void SwitchesBetweenKoreanAndEnglishResources()
    {
        var strings = UiStrings.Instance;
        var originalCulture = strings.SelectedLanguage.CultureName;
        try
        {
            strings.SelectCulture("ko-KR");
            Assert.Equal("준비 중…", strings["Startup.Status"]);

            strings.SelectCulture("en-US");
            Assert.Equal("Getting ready…", strings["Startup.Status"]);
            Assert.Equal("en-US", strings.CurrentCulture.Name);
        }
        finally
        {
            strings.SelectCulture(originalCulture);
        }
    }

    [Fact]
    public void ExposesOnlySupportedLanguageOptions()
    {
        var languages = UiStrings.Instance.Languages;

        Assert.Collection(
            languages,
            korean =>
            {
                Assert.Equal("ko-KR", korean.CultureName);
                Assert.Equal("한국어", korean.DisplayName);
            },
            english =>
            {
                Assert.Equal("en-US", english.CultureName);
                Assert.Equal("English", english.DisplayName);
            });
    }
}

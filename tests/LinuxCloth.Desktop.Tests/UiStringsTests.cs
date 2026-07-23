using Avalonia.Headless;
using LinuxCloth.Desktop.Localization;
using LinuxCloth.Desktop.ViewModels;

namespace LinuxCloth.Desktop.Tests;

[Collection(HeadlessUiTestGroup.Name)]
public sealed class UiStringsTests
{
    private readonly HeadlessUnitTestSession _session;

    public UiStringsTests(HeadlessUiFixture fixture)
    {
        _session = fixture.Session;
    }

    [Fact]
    public async Task SwitchesBetweenKoreanAndEnglishResources()
    {
        await _session.Dispatch(
            () =>
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
            },
            CancellationToken.None);
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

    [Fact]
    public async Task SetupPhaseCopyUpdatesWithSelectedLanguage()
    {
        await _session.Dispatch(
            () =>
            {
                var strings = UiStrings.Instance;
                var originalCulture = strings.SelectedLanguage.CultureName;
                using var phase = new SetupFlowPhaseItemViewModel(1, "Setup.Phase.System");
                try
                {
                    strings.SelectCulture("ko-KR");
                    phase.Update(isComplete: false, isCurrent: true);
                    Assert.Equal("시스템 확인", phase.Title);
                    Assert.Equal("진행 중", phase.Marker);

                    strings.SelectCulture("en-US");
                    Assert.Equal("System check", phase.Title);
                    Assert.Equal("In progress", phase.Marker);
                }
                finally
                {
                    strings.SelectCulture(originalCulture);
                }
            },
            CancellationToken.None);
    }
}

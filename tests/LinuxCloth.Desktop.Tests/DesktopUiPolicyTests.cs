using System.Globalization;
using System.Xml.Linq;

namespace LinuxCloth.Desktop.Tests;

public sealed class DesktopUiPolicyTests
{
    private static readonly HashSet<string> InteractiveElements =
    [
        "Button",
        "CheckBox",
        "ComboBox",
        "Expander",
        "ListBox",
        "NumericUpDown",
        "TextBox",
    ];

    private static readonly string RepositoryRoot = FindRepositoryRoot();

    public static TheoryData<string> InteractiveXamlFiles => new()
    {
        "src/LinuxCloth.Desktop/Controls/ServiceDetails.axaml",
        "src/LinuxCloth.Desktop/Views/MainWindow.axaml",
        "src/LinuxCloth.Desktop/Views/SetupWizardView.axaml",
        "src/LinuxCloth.Desktop/Views/ShellWindow.axaml",
    };

    [Theory]
    [MemberData(nameof(InteractiveXamlFiles))]
    public void InteractiveControlsHaveStableAutomationIdentifiers(string relativePath)
    {
        var document = XDocument.Load(Path.Combine(RepositoryRoot, relativePath));
        var interactive = document
            .Descendants()
            .Where(element => InteractiveElements.Contains(element.Name.LocalName))
            .ToArray();
        var missing = interactive
            .Where(element => string.IsNullOrWhiteSpace(AutomationId(element)))
            .Select(element => element.Name.LocalName)
            .ToArray();
        var duplicate = interactive
            .Select(AutomationId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToArray();

        Assert.True(missing.Length == 0, $"AutomationId 누락: {string.Join(", ", missing)} ({relativePath})");
        Assert.True(duplicate.Length == 0, $"AutomationId 중복: {string.Join(", ", duplicate)} ({relativePath})");
    }

    [Theory]
    [MemberData(nameof(InteractiveXamlFiles))]
    public void IconOnlyButtonsHaveAccessibleNames(string relativePath)
    {
        var document = XDocument.Load(Path.Combine(RepositoryRoot, relativePath));
        var unnamed = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => element.Descendants().Any(descendant => descendant.Name.LocalName == "PathIcon"))
            .Where(element => string.IsNullOrWhiteSpace(AutomationName(element)))
            .Select(static element => AutomationId(element))
            .ToArray();

        Assert.True(unnamed.Length == 0, $"접근 가능한 이름 누락: {string.Join(", ", unnamed)} ({relativePath})");
    }

    [Fact]
    public void PrimaryButtonColorsMeetTextContrastTarget()
    {
        var path = Path.Combine(
            RepositoryRoot,
            "src/LinuxCloth.Desktop/Styles/ThemeResources.axaml");
        var document = XDocument.Load(path);
        var dictionaries = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .Where(element => ElementKey(element) is "Light" or "Dark")
            .ToArray();

        Assert.Equal(2, dictionaries.Length);
        foreach (var dictionary in dictionaries)
        {
            var accent = ResourceColor(dictionary, "AccentBrush");
            var foreground = ResourceColor(dictionary, "TextOnAccentBrush");
            var ratio = ContrastRatio(accent, foreground);
            Assert.True(
                ratio >= 4.5,
                $"{ElementKey(dictionary)} primary contrast was {ratio:0.00}:1");
        }
    }

    private static string? AutomationId(XElement element) => element
        .Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName == "AutomationProperties.AutomationId")
        ?.Value;

    private static string? AutomationName(XElement element) => element
        .Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName == "AutomationProperties.Name")
        ?.Value;

    private static string? ElementKey(XElement element) => element
        .Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName == "Key")
        ?.Value;

    private static Rgb ResourceColor(XElement dictionary, string key)
    {
        var color = dictionary
            .Elements()
            .Single(element => string.Equals(ElementKey(element), key, StringComparison.Ordinal))
            .Attribute("Color")
            ?.Value ?? throw new InvalidOperationException($"{key} 색상 값이 없습니다.");
        return Rgb.Parse(color);
    }

    private static double ContrastRatio(Rgb first, Rgb second)
    {
        var lighter = Math.Max(first.Luminance, second.Luminance);
        var darker = Math.Min(first.Luminance, second.Luminance);
        return (lighter + 0.05) / (darker + 0.05);
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

    private readonly record struct Rgb(byte Red, byte Green, byte Blue)
    {
        public double Luminance =>
            (0.2126 * Linear(Red)) +
            (0.7152 * Linear(Green)) +
            (0.0722 * Linear(Blue));

        public static Rgb Parse(string value)
        {
            if (value.Length != 7 || value[0] != '#')
            {
                throw new FormatException($"지원하지 않는 색상 형식입니다: {value}");
            }

            return new Rgb(
                byte.Parse(value.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(value.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(value.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        private static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}

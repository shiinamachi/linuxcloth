using System.Globalization;
using System.Text;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Application.ImageBuilding;

public sealed class WindowsSetupLocaleDetector
{
    public const int MaximumLangIniBytes = 64 * 1024;

    private static readonly string[] LangIniPaths = ["/sources/lang.ini", "/SOURCES/LANG.INI"];
    private static readonly HashSet<string> KnownLocales = CultureInfo
        .GetCultures(CultureTypes.AllCultures)
        .Select(static culture => culture.Name)
        .Where(static name => name.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly IProcessRunner _processRunner;

    public WindowsSetupLocaleDetector(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<string> DetectAsync(
        string windowsIsoPath,
        string sevenZipPath,
        string bubblewrapPath,
        string extractionDirectory,
        CancellationToken cancellationToken = default)
    {
        var windowsIso = ImageBuildPathGuard.RequireRegularFile(
            windowsIsoPath,
            "Windows installation ISO");
        var sevenZip = ImageBuildPathGuard.RequireRegularFile(
            sevenZipPath,
            "7-Zip executable",
            requireExecutable: true);
        var bubblewrap = ImageBuildPathGuard.RequireRegularFile(
            bubblewrapPath,
            "Bubblewrap executable",
            requireExecutable: true);
        var directory = RequireExtractionDirectory(extractionDirectory);

        foreach (var entryPath in LangIniPaths)
        {
            var destination = Path.Combine(directory, Path.GetFileName(entryPath));
            var result = await _processRunner.RunAsync(
                    WindowsInstallationPlanner.BuildConfinedExtraction(
                        bubblewrap,
                        sevenZip,
                        windowsIso,
                        entryPath,
                        directory),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsSuccess || !File.Exists(destination))
            {
                File.Delete(destination);
                continue;
            }

            try
            {
                var langIni = ImageBuildPathGuard.RequireRegularFile(
                    destination,
                    "extracted Windows setup language metadata");
                var length = new FileInfo(langIni).Length;
                if (length is <= 0 or > MaximumLangIniBytes)
                {
                    throw new WindowsImageBuildException(
                        "The Windows setup language metadata has an invalid size.");
                }

                using var reader = new StreamReader(
                    langIni,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                return ParseLangIni(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            }
            finally
            {
                File.Delete(destination);
            }
        }

        throw new WindowsImageBuildException(
            "The Windows ISO does not contain sources/lang.ini setup language metadata.");
    }

    public static string ParseLangIni(string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contents);
        if (Encoding.UTF8.GetByteCount(contents) > MaximumLangIniBytes)
        {
            throw new WindowsImageBuildException(
                "The Windows setup language metadata exceeds its size limit.");
        }

        var inAvailableLanguages = false;
        foreach (var untrimmedLine in contents.Split(['\r', '\n']))
        {
            var line = untrimmedLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                inAvailableLanguages = string.Equals(
                    line[1..^1].Trim(),
                    "Available UI Languages",
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inAvailableLanguages)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var candidate = line[..separator].Trim();
            try
            {
                return NormalizeLocale(candidate);
            }
            catch (WindowsImageBuildException)
            {
                // Continue to another advertised Windows setup language.
            }
        }

        throw new WindowsImageBuildException(
            "The Windows setup language metadata contains no supported UI language.");
    }

    public static string NormalizeLocale(string candidate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        try
        {
            var locale = CultureInfo.GetCultureInfo(candidate).Name;
            if (locale.Length is > 0 and <= 32 &&
                KnownLocales.Contains(locale) &&
                locale.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            {
                return locale;
            }
        }
        catch (CultureNotFoundException)
        {
            // Report all invalid locale forms consistently below.
        }

        throw new WindowsImageBuildException("The Windows setup locale is invalid.");
    }

    private static string RequireExtractionDirectory(string path)
    {
        var directory = ImageBuildPathGuard.NormalizeAbsolute(path, "Windows setup language extraction directory");
        if (!Directory.Exists(directory) ||
            File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new WindowsImageBuildException(
                "The Windows setup language extraction directory is unavailable or unsafe.");
        }

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return directory;
    }
}

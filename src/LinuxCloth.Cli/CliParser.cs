using System.Globalization;
using LinuxCloth.Application.Images;
using LinuxCloth.Catalog;
using LinuxCloth.Core;

namespace LinuxCloth.Cli;

public static class CliParser
{
    public static CliParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return CliParseResult.Success(new HelpCommand());
        }

        if (arguments.Any(static argument => argument is "--help" or "-h"))
        {
            return CliParseResult.Success(new HelpCommand(GetHelpTopic(arguments)));
        }

        if (arguments.Count == 1 && arguments[0] is "--version" or "-V")
        {
            return CliParseResult.Success(new VersionCommand());
        }

        return arguments[0] switch
        {
            "doctor" => ParseParameterless(arguments, new DoctorCommand()),
            "catalog" => ParseCatalog(arguments),
            "image" => ParseImage(arguments),
            "cleanup" => ParseParameterless(arguments, new CleanupCommand()),
            "run" => ParseRun(arguments),
            _ => CliParseResult.Failure($"알 수 없는 명령입니다: {SafeArgument(arguments[0])}"),
        };
    }

    private static CliParseResult ParseCatalog(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2)
        {
            return CliParseResult.Failure("catalog 다음에 list 또는 search가 필요합니다.");
        }

        return arguments[1] switch
        {
            "list" => ParseCatalogQuery(arguments, search: false),
            "search" => ParseCatalogQuery(arguments, search: true),
            _ => CliParseResult.Failure($"알 수 없는 catalog 하위 명령입니다: {SafeArgument(arguments[1])}"),
        };
    }

    private static CliParseResult ParseCatalogQuery(
        IReadOnlyList<string> arguments,
        bool search)
    {
        CatalogCategory? category = null;
        string? catalogRoot = null;
        string? query = null;
        var seenCategory = false;
        var seenCatalogRoot = false;

        for (var index = 2; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--category":
                    if (seenCategory)
                    {
                        return CliParseResult.Failure("--category 옵션을 두 번 지정할 수 없습니다.");
                    }

                    if (!TryReadValue(arguments, ref index, argument, out var categoryValue, out var error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    if (!Enum.TryParse<CatalogCategory>(categoryValue, ignoreCase: true, out var parsedCategory) ||
                        !Enum.IsDefined(parsedCategory))
                    {
                        return CliParseResult.Failure(
                            $"지원하지 않는 카테고리입니다: {SafeArgument(categoryValue!)}");
                    }

                    category = parsedCategory;
                    seenCategory = true;
                    break;

                case "--catalog-root":
                    if (seenCatalogRoot)
                    {
                        return CliParseResult.Failure("--catalog-root 옵션을 두 번 지정할 수 없습니다.");
                    }

                    if (!TryReadValue(arguments, ref index, argument, out catalogRoot, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    seenCatalogRoot = true;
                    break;

                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        return CliParseResult.Failure($"알 수 없는 옵션입니다: {SafeArgument(argument)}");
                    }

                    if (!search)
                    {
                        return CliParseResult.Failure(
                            $"catalog list는 위치 인수를 받지 않습니다: {SafeArgument(argument)}");
                    }

                    if (query is not null)
                    {
                        return CliParseResult.Failure("검색어는 하나만 지정할 수 있습니다. 공백이 있으면 따옴표로 묶으세요.");
                    }

                    if (string.IsNullOrWhiteSpace(argument) || argument.Length > 256 || argument.Any(char.IsControl))
                    {
                        return CliParseResult.Failure("검색어는 1~256자의 제어 문자가 없는 문자열이어야 합니다.");
                    }

                    query = argument;
                    break;
            }
        }

        if (search && query is null)
        {
            return CliParseResult.Failure("catalog search에는 검색어가 필요합니다.");
        }

        return search
            ? CliParseResult.Success(new CatalogSearchCommand(query!, category, catalogRoot))
            : CliParseResult.Success(new CatalogListCommand(category, catalogRoot));
    }

    private static CliParseResult ParseImage(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 2)
        {
            return CliParseResult.Failure("image 다음에 list, verify 또는 build가 필요합니다.");
        }

        switch (arguments[1])
        {
            case "list":
                return arguments.Count == 2
                    ? CliParseResult.Success(new ImageListCommand())
                    : CliParseResult.Failure("image list는 인수나 옵션을 받지 않습니다.");
            case "verify":
                if (arguments.Count != 3)
                {
                    return CliParseResult.Failure("사용법: linuxcloth image verify <IMAGE_ID>");
                }

                return ImageId.TryParse(arguments[2], out var imageId)
                    ? CliParseResult.Success(new ImageVerifyCommand(imageId))
                    : CliParseResult.Failure("IMAGE_ID 형식이 올바르지 않습니다.");
            case "build":
                return ParseImageBuild(arguments);
            default:
                return CliParseResult.Failure(
                    $"알 수 없는 image 하위 명령입니다: {SafeArgument(arguments[1])}");
        }
    }

    private static CliParseResult ParseImageBuild(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 3)
        {
            return CliParseResult.Failure("image build 다음에 start, resume 또는 recover가 필요합니다.");
        }

        return arguments[2] switch
        {
            "start" => ParseImageBuildStart(arguments),
            "resume" => ParseImageBuildResume(arguments),
            "recover" => ParseImageBuildRecover(arguments),
            _ => CliParseResult.Failure(
                $"알 수 없는 image build 하위 명령입니다: {SafeArgument(arguments[2])}"),
        };
    }

    private static CliParseResult ParseImageBuildStart(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 4 || !ImageId.TryParse(arguments[3], out var imageId))
        {
            return CliParseResult.Failure(
                "사용법: linuxcloth image build start <IMAGE_ID> --windows-iso PATH --virtio-win-iso PATH --guest-bridge PATH");
        }

        string? windowsIsoPath = null;
        string? virtioWinIsoPath = null;
        string? guestBridgeExecutablePath = null;
        var diskSizeGiB = 96;
        var cpuCount = 4;
        var memoryMiB = 6144;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 4; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (!seen.Add(option))
            {
                return DuplicateOption(option);
            }

            string? value;
            string? error;
            switch (option)
            {
                case "--windows-iso":
                    if (!TryReadAbsolutePath(arguments, ref index, option, out windowsIsoPath, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    break;
                case "--virtio-win-iso":
                    if (!TryReadAbsolutePath(arguments, ref index, option, out virtioWinIsoPath, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    break;
                case "--guest-bridge":
                    if (!TryReadAbsolutePath(
                            arguments,
                            ref index,
                            option,
                            out guestBridgeExecutablePath,
                            out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    break;
                case "--disk-gib":
                    if (!TryReadValue(arguments, ref index, option, out value, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out diskSizeGiB) ||
                        diskSizeGiB is < 64 or > 1024)
                    {
                        return CliParseResult.Failure("--disk-gib는 64~1024의 정수여야 합니다.");
                    }

                    break;
                case "--cpus":
                    if (!TryReadValue(arguments, ref index, option, out value, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out cpuCount) ||
                        cpuCount is < 2 or > 32)
                    {
                        return CliParseResult.Failure("이미지 빌드의 --cpus는 2~32의 정수여야 합니다.");
                    }

                    break;
                case "--memory-mib":
                    if (!TryReadValue(arguments, ref index, option, out value, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out memoryMiB) ||
                        memoryMiB is < 4096 or > 131072)
                    {
                        return CliParseResult.Failure(
                            "이미지 빌드의 --memory-mib는 4096~131072의 정수여야 합니다.");
                    }

                    break;
                default:
                    return CliParseResult.Failure($"알 수 없는 옵션입니다: {SafeArgument(option)}");
            }
        }

        if (windowsIsoPath is null || virtioWinIsoPath is null || guestBridgeExecutablePath is null)
        {
            return CliParseResult.Failure(
                "이미지 빌드에는 --windows-iso, --virtio-win-iso, --guest-bridge가 모두 필요합니다.");
        }

        return CliParseResult.Success(new ImageBuildStartCommand(
            imageId,
            windowsIsoPath,
            virtioWinIsoPath,
            guestBridgeExecutablePath,
            diskSizeGiB,
            cpuCount,
            memoryMiB));
    }

    private static CliParseResult ParseImageBuildResume(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 4 || !ImageId.TryParse(arguments[3], out var imageId))
        {
            return CliParseResult.Failure(
                "사용법: linuxcloth image build resume <IMAGE_ID> --staging PATH");
        }

        var path = ParseSingleStagingOption(arguments, startIndex: 4);
        return path.Error is null
            ? CliParseResult.Success(new ImageBuildResumeCommand(imageId, path.Path!))
            : CliParseResult.Failure(path.Error);
    }

    private static CliParseResult ParseImageBuildRecover(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 4 || !ImageId.TryParse(arguments[3], out var imageId))
        {
            return CliParseResult.Failure(
                "사용법: linuxcloth image build recover <IMAGE_ID> --staging PATH");
        }

        var parsed = ParseSingleStagingOption(arguments, startIndex: 4);
        if (parsed.Error is not null)
        {
            return CliParseResult.Failure(parsed.Error);
        }

        return CliParseResult.Success(new ImageBuildRecoverCommand(
            imageId,
            parsed.Path!));
    }

    private static (string? Path, string? Error) ParseSingleStagingOption(
        IReadOnlyList<string> arguments,
        int startIndex)
    {
        string? stagingDirectory = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = startIndex; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (!seen.Add(option))
            {
                return (null, $"{option} 옵션을 두 번 지정할 수 없습니다.");
            }

            switch (option)
            {
                case "--staging":
                    if (!TryReadAbsolutePath(
                            arguments,
                            ref index,
                            option,
                            out stagingDirectory,
                            out var error))
                    {
                        return (null, error);
                    }

                    break;
                default:
                    return (null, $"알 수 없는 옵션입니다: {SafeArgument(option)}");
            }
        }

        return stagingDirectory is null
            ? (null, "--staging에 보존된 절대 staging 경로가 필요합니다.")
            : (stagingDirectory, null);
    }

    private static CliParseResult ParseRun(IReadOnlyList<string> arguments)
    {
        var serviceIds = new List<ServiceId>();
        ImageId imageId = default;
        var hasImage = false;
        var cpuCount = 4;
        var memoryMiB = 6144;
        var networkEnabled = true;
        var clipboardEnabled = false;
        string? catalogRoot = null;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--image":
                    if (!seenOptions.Add(argument))
                    {
                        return DuplicateOption(argument);
                    }

                    if (!TryReadValue(arguments, ref index, argument, out var imageValue, out var error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    if (!ImageId.TryParse(imageValue, out imageId))
                    {
                        return CliParseResult.Failure("--image의 IMAGE_ID 형식이 올바르지 않습니다.");
                    }

                    hasImage = true;
                    break;

                case "--cpus":
                    if (!seenOptions.Add(argument))
                    {
                        return DuplicateOption(argument);
                    }

                    if (!TryReadValue(arguments, ref index, argument, out var cpuValue, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    if (!int.TryParse(cpuValue, NumberStyles.None, CultureInfo.InvariantCulture, out cpuCount) ||
                        cpuCount is < 1 or > 64)
                    {
                        return CliParseResult.Failure("--cpus는 1~64의 정수여야 합니다.");
                    }

                    break;

                case "--memory-mib":
                    if (!seenOptions.Add(argument))
                    {
                        return DuplicateOption(argument);
                    }

                    if (!TryReadValue(arguments, ref index, argument, out var memoryValue, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    if (!int.TryParse(memoryValue, NumberStyles.None, CultureInfo.InvariantCulture, out memoryMiB) ||
                        memoryMiB is < 4096 or > 262144)
                    {
                        return CliParseResult.Failure("--memory-mib는 4096~262144의 정수여야 합니다.");
                    }

                    break;

                case "--no-network":
                    if (!seenOptions.Add(argument))
                    {
                        return DuplicateOption(argument);
                    }

                    networkEnabled = false;
                    break;

                case "--enable-clipboard":
                    if (!seenOptions.Add(argument))
                    {
                        return DuplicateOption(argument);
                    }

                    clipboardEnabled = true;
                    break;

                case "--catalog-root":
                    if (!seenOptions.Add(argument))
                    {
                        return DuplicateOption(argument);
                    }

                    if (!TryReadValue(arguments, ref index, argument, out catalogRoot, out error))
                    {
                        return CliParseResult.Failure(error!);
                    }

                    break;

                default:
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                    {
                        return CliParseResult.Failure($"알 수 없는 옵션입니다: {SafeArgument(argument)}");
                    }

                    if (!ServiceId.TryCreate(argument, out var serviceId))
                    {
                        return CliParseResult.Failure(
                            $"서비스 ID 형식이 올바르지 않습니다: {SafeArgument(argument)}");
                    }

                    if (serviceIds.Contains(serviceId))
                    {
                        return CliParseResult.Failure(
                            $"서비스 ID를 중복 지정할 수 없습니다: {SafeArgument(argument)}");
                    }

                    serviceIds.Add(serviceId);
                    if (serviceIds.Count > 32)
                    {
                        return CliParseResult.Failure("한 세션에는 최대 32개의 서비스만 지정할 수 있습니다.");
                    }

                    break;
            }
        }

        if (serviceIds.Count == 0)
        {
            return CliParseResult.Failure("run에는 하나 이상의 서비스 ID가 필요합니다.");
        }

        if (!hasImage)
        {
            return CliParseResult.Failure("run에는 --image <IMAGE_ID>가 필요합니다.");
        }

        return CliParseResult.Success(new RunCommand(
            serviceIds.AsReadOnly(),
            imageId,
            cpuCount,
            memoryMiB,
            networkEnabled,
            clipboardEnabled,
            catalogRoot));
    }

    private static CliParseResult ParseParameterless(
        IReadOnlyList<string> arguments,
        CliCommand command) =>
        arguments.Count == 1
            ? CliParseResult.Success(command)
            : CliParseResult.Failure($"{arguments[0]} 명령은 인수나 옵션을 받지 않습니다.");

    private static CliParseResult DuplicateOption(string option) =>
        CliParseResult.Failure($"{option} 옵션을 두 번 지정할 수 없습니다.");

    private static bool TryReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        if (index + 1 >= arguments.Count ||
            arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = null;
            error = $"{option} 옵션에 값이 필요합니다.";
            return false;
        }

        value = arguments[++index];
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl))
        {
            error = $"{option} 옵션 값이 비어 있거나 너무 길거나 제어 문자를 포함합니다.";
            value = null;
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadAbsolutePath(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        out string? path,
        out string? error)
    {
        if (!TryReadValue(arguments, ref index, option, out var value, out error))
        {
            path = null;
            return false;
        }

        if (value is null || !Path.IsPathFullyQualified(value))
        {
            path = null;
            error = $"{option}에는 절대 경로가 필요합니다.";
            return false;
        }

        try
        {
            path = Path.GetFullPath(value);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            path = null;
            error = $"{option} 경로가 올바르지 않습니다.";
            return false;
        }
    }

    private static string? GetHelpTopic(IReadOnlyList<string> arguments)
    {
        var words = arguments
            .TakeWhile(static argument => argument is not "--help" and not "-h")
            .Where(static argument => !argument.StartsWith('-'))
            .Take(2)
            .ToArray();
        return words.Length == 0 ? null : string.Join(' ', words);
    }

    private static string SafeArgument(string value)
    {
        var safe = new string(value.Take(128).Select(static character =>
            char.IsControl(character) ? '\uFFFD' : character).ToArray());
        return value.Length > 128 ? $"{safe}…" : safe;
    }
}

namespace LinuxCloth.Runtime.Qemu.Processes;

public sealed record ProcessSpec
{
    public ProcessSpec(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? standardOutputPath = null,
        string? standardErrorPath = null,
        bool inheritEnvironment = false,
        string? identityExecutablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        FileName = fileName;
        Arguments = arguments?.ToArray() ?? [];
        WorkingDirectory = workingDirectory;
        Environment = environment is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(environment, StringComparer.Ordinal);
        StandardOutputPath = standardOutputPath;
        StandardErrorPath = standardErrorPath;
        InheritEnvironment = inheritEnvironment;
        if (identityExecutablePath is not null &&
            (string.IsNullOrWhiteSpace(identityExecutablePath) ||
             !Path.IsPathFullyQualified(identityExecutablePath) ||
             identityExecutablePath.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "The expected identity executable path must be absolute.",
                nameof(identityExecutablePath));
        }

        IdentityExecutablePath = identityExecutablePath is null
            ? null
            : Path.GetFullPath(identityExecutablePath);
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string?> Environment { get; }

    public string? StandardOutputPath { get; }

    public string? StandardErrorPath { get; }

    public bool InheritEnvironment { get; }

    public string? IdentityExecutablePath { get; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}

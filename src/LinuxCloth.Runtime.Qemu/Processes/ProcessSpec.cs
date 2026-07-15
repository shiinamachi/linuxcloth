namespace LinuxCloth.Runtime.Qemu.Processes;

public sealed record ProcessSpec
{
    public ProcessSpec(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        FileName = fileName;
        Arguments = arguments?.ToArray() ?? [];
        WorkingDirectory = workingDirectory;
        Environment = environment is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(environment, StringComparer.Ordinal);
    }

    public string FileName { get; }

    public IReadOnlyList<string> Arguments { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyDictionary<string, string?> Environment { get; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool IsSuccess => ExitCode == 0;
}


namespace LinuxCloth.Wsb;

public sealed record WsbMappedFolder
{
    public WsbMappedFolder(string hostFolder, string? sandboxFolder = null, bool readOnly = false)
    {
        HostFolder = ValidatePath(hostFolder, nameof(hostFolder));
        SandboxFolder = sandboxFolder is null ? null : ValidatePath(sandboxFolder, nameof(sandboxFolder));
        ReadOnly = readOnly;
    }

    public string HostFolder { get; }

    public string? SandboxFolder { get; }

    public bool ReadOnly { get; }

    private static string ValidatePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length > 4096 || value.Contains('\0'))
        {
            throw new ArgumentException("A mapped-folder path is invalid or too long.", parameterName);
        }

        return value;
    }
}

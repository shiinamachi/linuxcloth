namespace LinuxCloth.Wsb;

public sealed class LaunchManifestValidationException : FormatException
{
    public LaunchManifestValidationException(string message)
        : base(message)
    {
    }

    public LaunchManifestValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

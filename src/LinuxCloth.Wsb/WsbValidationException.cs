namespace LinuxCloth.Wsb;

public sealed class WsbValidationException : FormatException
{
    public WsbValidationException(string message)
        : base(message)
    {
    }

    public WsbValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

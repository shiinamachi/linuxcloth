namespace LinuxCloth.Application.Images;

public class ImageRegistryException : Exception
{
    public ImageRegistryException(string message)
        : base(message)
    {
    }

    public ImageRegistryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ImageMetadataValidationException : ImageRegistryException
{
    public ImageMetadataValidationException(string message)
        : base(message)
    {
    }

    public ImageMetadataValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

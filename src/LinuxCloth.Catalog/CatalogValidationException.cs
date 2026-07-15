namespace LinuxCloth.Catalog;

public sealed class CatalogValidationException : Exception
{
    public CatalogValidationException()
    {
    }

    public CatalogValidationException(string message)
        : base(message)
    {
    }

    public CatalogValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

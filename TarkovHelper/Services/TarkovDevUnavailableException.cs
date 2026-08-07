namespace TarkovHelper.Services;

/// <summary>
/// Indicates a temporary tarkov.dev/API infrastructure outage.
/// The existing local database remains valid and must not be replaced.
/// </summary>
public sealed class TarkovDevUnavailableException : Exception
{
    public TarkovDevUnavailableException(string message)
        : base(message)
    {
    }

    public TarkovDevUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

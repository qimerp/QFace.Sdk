namespace QimErp.Shared.Common.Services.Notifications.Registry;

public sealed class RegistryStorageUnavailableException : Exception
{
    public RegistryStorageUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class RegistryKeyRejectedException : ArgumentException
{
    public RegistryKeyRejectedException(string message)
        : base(message)
    {
    }
}

public sealed class RegistryObjectTooLargeException : Exception
{
    public RegistryObjectTooLargeException(string message)
        : base(message)
    {
    }
}

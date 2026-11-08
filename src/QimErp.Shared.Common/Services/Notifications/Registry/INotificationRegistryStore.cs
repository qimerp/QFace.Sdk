namespace QimErp.Shared.Common.Services.Notifications.Registry;

public interface INotificationRegistryStore
{
    /// <summary>Returns null when the object does not exist. Throws <see cref="RegistryStorageUnavailableException"/> on transient failure.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);
}

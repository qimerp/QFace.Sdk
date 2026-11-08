using QFace.Sdk.BlobStorage.Services;

namespace QimErp.Shared.Common.Services.Notifications.Registry;

/// <summary><see cref="IFileUploadService.GetObjectBytesAsync"/> returns null for both 404 and 400, so either reads as not found.</summary>
public sealed class BlobRegistryStore : INotificationRegistryStore
{
    private readonly IFileUploadService _files;

    public BlobRegistryStore(IFileUploadService files)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        RegistryKey.SplitOrThrow(key);
        try
        {
            var body = await _files.GetObjectBytesAsync(key, ct);
            if (body is not null && body.Length > RegistryKey.MaxObjectBytes)
                throw new RegistryObjectTooLargeException("Registry object exceeds the size limit.");
            return body;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RegistryObjectTooLargeException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new RegistryStorageUnavailableException("Registry blob storage read failed.", error);
        }
    }
}

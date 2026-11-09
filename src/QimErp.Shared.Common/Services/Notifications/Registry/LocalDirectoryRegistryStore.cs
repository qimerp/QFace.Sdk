namespace QimErp.Shared.Common.Services.Notifications.Registry;

public sealed class LocalDirectoryRegistryStore : INotificationRegistryStore
{
    private const int MaxLinkHops = 40;

    private readonly string _root;

    public LocalDirectoryRegistryStore(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A registry root directory is required.", nameof(root));

        _root = RealPath(Path.GetFullPath(root));
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        var segments = RegistryKey.SplitOrThrow(key);
        var path = Path.GetFullPath(Path.Combine([_root, .. segments]));
        if (!IsUnderRoot(path))
            throw new RegistryKeyRejectedException("Registry key resolves outside the registry root.");

        try
        {
            var resolved = RealPath(path);
            if (!IsUnderRoot(resolved))
                throw new RegistryKeyRejectedException("Registry key resolves outside the registry root.");

            if (!File.Exists(resolved))
                return null;

            await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (stream.Length > RegistryKey.MaxObjectBytes)
                throw new RegistryObjectTooLargeException("Registry object exceeds the size limit.");

            var body = new byte[stream.Length];
            await stream.ReadExactlyAsync(body, ct);
            if (stream.ReadByte() != -1)
                throw new RegistryStorageUnavailableException("Registry object changed while it was read.");

            return body;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException error)
        {
            throw new RegistryStorageUnavailableException("Registry storage read failed.", error);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new RegistryStorageUnavailableException("Registry storage read failed.", error);
        }
    }

    private bool IsUnderRoot(string path) =>
        path.StartsWith(_root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string RealPath(string fullPath)
    {
        var hops = 0;
        return Resolve(fullPath, ref hops);
    }

    private static string Resolve(string fullPath, ref int hops)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var parts = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = Path.Combine(current, parts[i]);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (!info.Exists && info.LinkTarget is null)
                return Path.Combine([candidate, .. parts[(i + 1)..]]);

            if (info.LinkTarget is null)
            {
                current = candidate;
                continue;
            }

            if (++hops > MaxLinkHops)
                throw new RegistryKeyRejectedException("Registry path has too many symbolic links.");

            var target = Path.GetFullPath(info.LinkTarget, current);
            current = Resolve(target, ref hops);
        }

        return current;
    }
}

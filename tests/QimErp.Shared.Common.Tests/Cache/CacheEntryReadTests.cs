using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using QFace.Sdk.RedisCache.Services;
using RedisCacheService = QimErp.Shared.Common.Services.Cache.RedisCacheService;
using QimErp.Shared.Common.Contracts;
using QimErp.Shared.Common.Services.Cache;
using QimErp.Shared.Common.Tests.TenantSetup;
using Xunit;

namespace QimErp.Shared.Common.Tests.Cache;

public class CacheEntryReadTests
{
    private const string Key = "qface:qimerp:test:key";
    private static readonly TenantModuleSnapshotEntry Snapshot = new(1, ["CoreHR"]);

    [Fact]
    public async Task Hit_WhenValuePresent()
    {
        var redis = new Mock<IRedisCacheService>();
        redis.Setup(r => r.GetAsync<TenantModuleSnapshotEntry>(Key)).ReturnsAsync(Snapshot);

        var read = await Service(redis).ReadEntryAsync<TenantModuleSnapshotEntry>(Key);

        read.Status.Should().Be(CacheReadStatus.Hit);
        read.Value.Should().Be(Snapshot);
    }

    [Fact]
    public async Task Miss_WhenKeyAbsent()
    {
        var redis = new Mock<IRedisCacheService>();
        redis.Setup(r => r.GetAsync<TenantModuleSnapshotEntry>(Key)).ReturnsAsync((TenantModuleSnapshotEntry)null!);
        redis.Setup(r => r.ExistsAsync(Key)).ReturnsAsync(false);

        (await Service(redis).ReadEntryAsync<TenantModuleSnapshotEntry>(Key)).Status.Should().Be(CacheReadStatus.Miss);
    }

    [Fact]
    public async Task Unavailable_WhenReadThrows()
    {
        var redis = new Mock<IRedisCacheService>();
        redis.Setup(r => r.GetAsync<TenantModuleSnapshotEntry>(Key)).ThrowsAsync(new TimeoutException());

        var service = Service(redis);

        (await service.ReadEntryAsync<TenantModuleSnapshotEntry>(Key)).Status.Should().Be(CacheReadStatus.Unavailable);
        (await service.GetAsync<TenantModuleSnapshotEntry>(Key)).Should().BeNull();
    }

    [Fact]
    public async Task Unavailable_WhenKeyExistsButCannotBeDecoded()
    {
        var redis = new Mock<IRedisCacheService>();
        redis.Setup(r => r.GetAsync<TenantModuleSnapshotEntry>(Key)).ReturnsAsync((TenantModuleSnapshotEntry)null!);
        redis.Setup(r => r.ExistsAsync(Key)).ReturnsAsync(true);

        (await Service(redis).ReadEntryAsync<TenantModuleSnapshotEntry>(Key)).Status.Should().Be(CacheReadStatus.Unavailable);
    }

    [Fact]
    public async Task Miss_WhenCacheDisabled()
    {
        var redis = new Mock<IRedisCacheService>(MockBehavior.Strict);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RedisCache:Enabled"] = "false" })
            .Build();

        var read = await new RedisCacheService(redis.Object, NullLogger<RedisCacheService>.Instance, configuration)
            .ReadEntryAsync<TenantModuleSnapshotEntry>(Key);

        read.Status.Should().Be(CacheReadStatus.Miss);
    }

    [Fact]
    public async Task NonReaderCache_ReportsNullAsMiss_AndValueAsHit()
    {
        var cache = new InMemoryDistributedCacheService();

        (await cache.ReadEntryAsync<TenantModuleSnapshotEntry>(Key)).Status.Should().Be(CacheReadStatus.Miss);

        await cache.SetAsync(Key, Snapshot, expiration: null);

        var read = await cache.ReadEntryAsync<TenantModuleSnapshotEntry>(Key);
        read.Status.Should().Be(CacheReadStatus.Hit);
        read.Value.Should().Be(Snapshot);
    }

    private static RedisCacheService Service(Mock<IRedisCacheService> redis) =>
        new(redis.Object, NullLogger<RedisCacheService>.Instance, new ConfigurationBuilder().Build());
}

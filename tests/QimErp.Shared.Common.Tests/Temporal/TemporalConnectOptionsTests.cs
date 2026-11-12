using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using QFace.Sdk.Temporal.Extensions;
using QFace.Sdk.Temporal.Options;
using Temporalio.Client;
using Xunit;

namespace QimErp.Shared.Common.Tests.Temporal;

// TEMPORAL_* environment variables are process-wide, so these tests must not run beside each other.
[Collection(nameof(TemporalConnectOptionsTests))]
[CollectionDefinition(nameof(TemporalConnectOptionsTests), DisableParallelization = true)]
public sealed class TemporalConnectOptionsTests : IDisposable
{
    private const string Remote = "temporal.internal.example:7233";
    private const string Local = "localhost:7233";

    private readonly Dictionary<string, string?> _environment = new();

    public TemporalConnectOptionsTests()
    {
        foreach (var name in new[] { "TEMPORAL_ADDRESS", "TEMPORAL_NAMESPACE", "TEMPORAL_API_KEY" })
        {
            _environment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void An_explicit_false_wins_for_a_non_local_address()
    {
        var connect = Build(new TemporalOptions { Address = Remote, EnableTls = false });

        connect.TargetHost.Should().Be(Remote);
        connect.Tls.Should().BeNull("EnableTls: false must not be overridden because the address is remote");
        connect.ApiKey.Should().BeNull();
        connect.RpcMetadata.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData(Remote)]
    [InlineData(Local)]
    public void An_explicit_false_wins_when_an_api_key_is_set_and_the_key_travels_as_bearer_metadata(string address)
    {
        var connect = Build(new TemporalOptions { Address = address, EnableTls = false, ApiKey = "  key-123  " });

        connect.Tls.Should().BeNull("EnableTls: false must not be overridden because an API key is set");
        connect.ApiKey.Should().BeNull("the native API key option needs TLS");
        connect.RpcMetadata.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("authorization", "Bearer key-123"));
    }

    [Fact]
    public void An_explicit_false_wins_over_an_api_key_from_the_environment()
    {
        Environment.SetEnvironmentVariable("TEMPORAL_API_KEY", "env-key");

        var connect = Build(new TemporalOptions { Address = Remote, EnableTls = false });

        connect.Tls.Should().BeNull();
        connect.RpcMetadata.Should().Contain("authorization", "Bearer env-key");
    }

    [Fact]
    public void An_explicit_false_bound_from_configuration_is_not_read_as_unset()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Temporal:Address"] = Remote,
                ["Temporal:EnableTls"] = "false"
            })
            .Build();
        var options = configuration.GetSection(TemporalOptions.SectionName).Get<TemporalOptions>()!;

        options.EnableTls.Should().BeFalse();
        Build(options).Tls.Should().BeNull();
    }

    [Fact]
    public void An_unset_value_turns_tls_on_for_a_non_local_address()
    {
        var connect = Build(new TemporalOptions { Address = Remote });

        connect.Tls.Should().NotBeNull();
        connect.Tls!.Domain.Should().Be("temporal.internal.example");
        connect.ApiKey.Should().BeNull();
    }

    [Fact]
    public void An_unset_value_turns_tls_on_when_an_api_key_is_set_even_for_a_local_address()
    {
        var connect = Build(new TemporalOptions { Address = Local, ApiKey = "key-123" });

        connect.Tls.Should().NotBeNull();
        connect.ApiKey.Should().Be("key-123");
        connect.RpcMetadata.Should().BeNullOrEmpty();
    }

    [Theory]
    [InlineData("localhost:7233")]
    [InlineData("127.0.0.1:7233")]
    public void An_unset_value_keeps_a_local_address_without_a_key_in_plaintext(string address)
    {
        var connect = Build(new TemporalOptions { Address = address });

        connect.Tls.Should().BeNull();
        connect.RpcMetadata.Should().BeNullOrEmpty();
    }

    [Fact]
    public void An_unset_value_bound_from_configuration_stays_unset()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Temporal:Address"] = Remote })
            .Build();

        configuration.GetSection(TemporalOptions.SectionName).Get<TemporalOptions>()!.EnableTls.Should().BeNull();
    }

    [Fact]
    public void An_explicit_true_turns_tls_on_for_a_local_address()
    {
        var connect = Build(new TemporalOptions { Address = Local, EnableTls = true });

        connect.Tls.Should().NotBeNull();
        connect.Tls!.Domain.Should().Be("localhost");
    }

    [Fact]
    public void The_option_is_a_nullable_boolean()
    {
        typeof(TemporalOptions).GetProperty(nameof(TemporalOptions.EnableTls))!.PropertyType
            .Should().Be(typeof(bool?));
    }

    private static TemporalClientConnectOptions Build(TemporalOptions options) =>
        TemporalClientServiceCollectionExtensions.BuildConnectOptions(options, NullLogger.Instance);

    public void Dispose()
    {
        foreach (var (name, value) in _environment)
            Environment.SetEnvironmentVariable(name, value);
    }
}

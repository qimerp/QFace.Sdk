# QFace SDK

A collection of .NET libraries designed to simplify complex development tasks and provide robust, scalable solutions for modern applications.

[![NuGet](https://img.shields.io/nuget/v/QFace.Sdk.ActorSystems.svg)](https://www.nuget.org/packages/QFace.Sdk.ActorSystems/)
[![Build Status](https://img.shields.io/github/workflow/status/qface/sdk/build)](https://github.com/qface/sdk/actions)
[![License](https://img.shields.io/github/license/qface/sdk)](LICENSE)

## Libraries

| Library                                          | Description                                                                              | NuGet                                                                                                                         |
| ------------------------------------------------ | ---------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| [QFace.Sdk.ActorSystems](docs/actor-system.md)   | Lightweight wrapper around Akka.NET for actor-based systems                              | [![NuGet](https://img.shields.io/nuget/v/QFace.Sdk.ActorSystems.svg)](https://www.nuget.org/packages/QFace.Sdk.ActorSystems/) |
| [QFace.Sdk.AI](docs/ai-sdk.md)                   | Provider-agnostic LLM + forecasting SDK                                                  |                                                                                                                               |
| [QFace.Sdk.BlobStorage](docs/blob-storage.md)    | Blob/object storage helpers                                                              |                                                                                                                               |
| [QFace.Sdk.ElasticSearch](docs/elasticsearch.md) | Elasticsearch client helpers                                                             |                                                                                                                               |
| [QFace.Sdk.Extensions](docs/extensions.md)       | Common extension methods                                                                 |                                                                                                                               |
| [QFace.Sdk.Kafka](docs/kafka-streaming.md)       | Kafka producer helpers (consumers removed; see docs/consumer-pipeline-removal.md)        |                                                                                                                               |
| [QFace.Sdk.Logging](docs/logging.md)             | Structured logging helpers                                                               |                                                                                                                               |
| [QFace.Sdk.MongoDb](docs/mongodb.md)             | MongoDB data access (+ `QFace.Sdk.MongoDb.MultiTenant`)                                  |                                                                                                                               |
| QFace.Sdk.RedisCache                             | Redis caching helpers — see [docs](docs/redis-cache-remove-by-pattern-implementation.md) |                                                                                                                               |
| QFace.Sdk.RedisMq                                | Redis pub/sub publisher (consumers removed; see docs/consumer-pipeline-removal.md)       |                                                                                                                               |
| [QFace.Sdk.SendMessage](docs/send-message.md)    | Send-message abstractions                                                                |                                                                                                                               |
| QFace.Sdk.Temporal                               | Temporal helpers (see `QimErp.Shared.Common` usage)                                      |                                                                                                                               |
| QFace.WebApi.Template                            | Project template for new WebApi SDK consumers                                            |                                                                                                                               |
| QFace.Sdk.Messaging                              | Unified API for message queues / event brokers                                           | _Coming soon_                                                                                                                 |
| QFace.Sdk.DataAccess                             | Flexible data access layer supporting multiple databases                                 | _Coming soon_                                                                                                                 |

> `QimErp.Shared.Common` and `QimErp.Shared.DemoData` also build out of this repo's `src/`, but they are QimERP platform libraries, not QFace SDK — see the platform docs (`../docs/shared/MODULES.md`).

## Quick Start

### Actor System SDK

```bash
dotnet add package QFace.Sdk.ActorSystems
```

```csharp
// Register actor system
builder.Services.AddActorSystem(
    new[] { typeof(Program).Assembly },
    config =>
    {
        config.SystemName = "MySystem";
        config.AddActorType<MyActor>();
    },
    addLifecycle: true);

// Use in your service
public class MyService
{
    private readonly IActorService _actorService;

    public MyService(IActorService actorService)
    {
        _actorService = actorService;
    }

    public void DoSomething()
    {
        _actorService.Tell<MyActor>(new MyMessage());
    }
}
```

## Documentation

For detailed documentation, visit:

- [SDK Overview](docs/index.md)
- [Actor System SDK](docs/actor-system.md)
- [Installation Guide](docs/shared/installation.md)
- [Configuration Patterns](docs/shared/configuration.md)

## Workflow approval gating: rollout prerequisite (Platform first)

`QimErp.Shared.Common` refuses a save of a workflow-enabled entity with
`WorkflowApprovalStateUnavailableException` (HTTP 503) unless it can prove whether approval is
needed. A missing workflow configuration or definition key proves "nothing bound" only when the
tenant warm marker (`qface:qimerp:workflow:warm:{tenantId}`) is a version 2 marker
(`{ "warmedAt": ..., "version": 2, "bound": ["{module}:{entityType}", ...] }`), younger than
`Workflow:WarmMarkerMaxAge` (default 48h), and the entity's pair is not in `bound`.

Platform must ship these, in this order, before any service bumps the package:

1. Write workflow configuration and definition keys without a TTL.
2. Re-warm them daily.
3. After every successful full warm, write the version 2 warm marker with a 48h TTL.
4. Rewrite the configuration whenever a definition is published.
5. When a binding is deleted, write a disabled configuration instead of removing the key.

Until the version 2 marker is written, saves of workflow-enabled entities with nothing bound
return 503.

## Consumers: test suites

There is no runtime switch to turn the marker rule off. Test projects use the
`QimErp.Shared.Common.Testing` package, which fails the build (`QIMTEST001`) in any project
without `<IsTestProject>true</IsTestProject>`:

- Suites with an in-memory shared cache: seed a version 2 marker.

  ```csharp
  await WorkflowCacheTestSupport.SeedTenantWarmMarkerAsync(sharedCache, tenantId, boundPairs: []);
  ```

- Suites with a no-op cache or `RedisCache:Enabled=false` (writes are dropped): wrap the
  registered `IWorkflowConfigCacheService` so the test tenants report a warm cache.

  ```csharp
  services.AddWarmWorkflowTenants(tenantId);
  ```

  `WarmTenantsWorkflowConfigCache` is the decorator behind it, for suites that build the
  config cache by hand.

## Samples

Check out the [samples](samples) directory for complete working examples of each SDK.

## Requirements

- .NET 6.0 or later
- For Actor System SDK: Akka.NET 1.4.46 or later

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

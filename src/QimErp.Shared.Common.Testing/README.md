# QimErp.Shared.Common.Testing

Test-only helpers for approval gating in `QimErp.Shared.Common`. A project that references
this package fails to build (`QIMTEST001`) unless it sets `<IsTestProject>true</IsTestProject>`
(`Microsoft.NET.Test.Sdk` sets it for test projects).

- `WorkflowCacheTestSupport.SeedTenantWarmMarkerAsync(sharedCache, tenantId, boundPairs)` writes a
  version 2 warm marker through the shared cache. Pairs (`"{module}:{entityType}"`) listed in
  `boundPairs` stay refused when their keys are missing.
- `services.AddWarmWorkflowTenants(tenantIds)` wraps the registered `IWorkflowConfigCacheService`
  so those tenants report a warm cache. Use it with a no-op cache or `RedisCache:Enabled=false`,
  where seeded keys are dropped.

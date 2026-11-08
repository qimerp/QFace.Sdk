# Redis: shared (cross-service) keys vs `RedisCache:KeyPrefix`

QFace.Sdk.RedisCache 1.0.4 · QimErp.Shared.Common 3.11.48 · 2026-11-08

## Problem

`QFace.Sdk.RedisCache.Services.RedisCacheService.GetFullKey` prepends
`RedisCacheOptions.KeyPrefix` to every key. CoreHR sets `"KeyPrefix": "corehr:"` and
Finance sets `"accounting:"`. Platform has no prefix and writes the shared keys verbatim, so
CoreHR reads `corehr:qface:qimerp:{tenant}:shared:tenant_module_snapshot`, misses, and
`RequiredModuleMiddleware` / `WorkflowModuleApprovalGate` treat the module as not
installed. Seen on Contabo dev, 2026-11-08. The pilot set `RedisCache__KeyPrefix=` (empty)
for CoreHR as a workaround, which also un-namespaces every CoreHR private key.

## Inventory: keys written by one service and read by another

| Key (physical) | Writer | Readers (code in Shared.Common unless noted) |
| --- | --- | --- |
| `qface:qimerp:{t}:shared:tenant_module_snapshot` | Platform `TenantModuleManifestService` | `TenantModuleAccessService` → `RequiredModuleMiddleware`, module gates, every service |
| `qface:qimerp:{t}:shared:tenant_plugin_snapshot` | Platform | `TenantPluginAccessService` |
| `qface:qimerp:{t}:shared:tenant_branding` | Platform `TenantRepository` | Payroll `PayslipPdfService`, Learning `CertificateBrandingProvider` (service code) |
| `qface:qimerp:shared:platform_ai_provider_config` | Platform `PlatformAIConfigSeedService` | `CachedAIOptionsProvider` |
| `qface:qimerp:{t}:shared:tenant_ai_provider_config` | Platform `TenantAIProviderSettingsService` | `CachedAIOptionsProvider` |
| `qface:qimerp:{t}:shared:tenant_microsoft365_config` | Platform `TenantMicrosoft365SettingsService` | `Microsoft365GraphClient` |
| `qface:qimerp:{t}:shared:tenant_google_workspace_config` | Platform `TenantGoogleWorkspaceSettingsService` | `GoogleCalendarClient` |
| `qface:qimerp:workflow:config:{t}:{module}:{entity}` | Platform (`WorkflowCacheConstants.WorkflowConfiguration`, same string) | `WorkflowConfigCacheService` |
| `qface:qimerp:workflow:definition:{t}:{code}:{entity}` and `…:definition_by_entity:{t}:{entity}` | Platform `EntityWorkflowDefinitionPublisher`, plus `WorkflowDefinitionCacheWriter` called by CoreHR and Performance seeders | `WorkflowDefinitionProvider` |

Key builders: `SharedCacheKeys` (all `:shared:` keys) and `WorkflowDefinitionCacheKeys`.

Keys under `qface:qimerp:` that are **service-private** and must stay prefixed:

- CoreHR: `HrCacheConstants` and `LeaveCacheConstants` (`qface:qimerp:{t}:hr:*`, `qface:qimerp:{t}:leave:*`), and `ImportService` in Shared.Common (`qface:qimerp:{t}:hr:import_*`).
- Platform: `PlatformCacheConstants`, `SharedConstants.Auth`, the rest of `WorkflowCacheConstants` (`qface:qimerp:{t}:auth:*`, `qface:qimerp:workflow:{t}:*`, and so on).
- Shared.Common: `KnowledgeCache.AskRateLimit` (`qface:qimerp:knowledge:*`) and `RedisActivationTokenService` (`qface:qimerp:auth:activation_token_*`).

So a rule like "never prefix keys starting with `qface:qimerp:`" is unsafe. It would move
all CoreHR private keys into the global namespace, so another service with a key of the same
shape would share CoreHR's entries. A CoreHR `RemoveByPatternAsync("qface:qimerp:{t}:*")`
style purge would also start deleting Platform's keys.

## Design

Whether a key is shared is decided **at the call site, by the service type it injects**.
It is not inferred from the key string. Physical key strings do not change.

**QFace.Sdk.RedisCache 1.0.4**

- New `IGlobalRedisCacheService : IRedisCacheService` (no new members). Its implementation,
  `GlobalRedisCacheService`, uses keys and patterns verbatim and never applies `KeyPrefix`.
- `AddRedisCache` registers it next to `IRedisCacheService` (scoped, same provider
  singleton). `IRedisCacheService` behaviour is unchanged.

**QimErp.Shared.Common 3.11.48**

- New `ISharedDistributedCacheService : IDistributedCacheService` (no new members), backed by
  `SharedRedisCacheService` over `IGlobalRedisCacheService`. It is registered in the same place as
  `IDistributedCacheService`.
- Every Shared.Common reader and writer of the keys in the table above now depends on
  `ISharedDistributedCacheService`: `TenantModuleAccessService`, `TenantPluginAccessService`,
  `CachedAIOptionsProvider`, `Microsoft365GraphClient`, `GoogleCalendarClient`,
  `WorkflowConfigCacheService`, `WorkflowDefinitionProvider` and `WorkflowDefinitionCacheWriter`.
- Rule for new code: a key from `SharedCacheKeys` or `WorkflowDefinitionCacheKeys` goes through
  `ISharedDistributedCacheService` (or `IGlobalRedisCacheService`). Everything else uses the
  prefixed services.

Rejected alternatives:

- **Prefix allowlist or regex in config.** This is implicit and per-service config that can
  drift. It cannot express `qface:qimerp:{t}:shared:*` as a plain prefix, and it silently
  captures any future private key that matches.
- **Marker inside the key string** (for example `~global:`). Key strings also flow into
  logs, `HttpContext.Items`, L1 dictionaries and raw StackExchange use. A marker that leaks
  there writes a literal `~global:` key. Changing the physical key would also orphan the
  1-year-TTL module snapshots, which are rebuilt only on install or onboarding, and that
  would return 403 on every gated route.

## Migration and back-compat

- **Physical keys are unchanged.** Platform (no prefix) writes the same strings, so a
  Platform bump changes nothing at runtime. Services without a prefix (Payroll, Learning,
  Performance, Operations, Operator, Reporting) see no runtime change either.
- **CoreHR and Finance** start reading Platform's real shared keys as soon as they run on
  3.11.48. After deploying, remove the Contabo env override `RedisCache__KeyPrefix=` from
  CoreHR so its private keys return to `corehr:`. The CoreHR private cache starts cold once,
  which is harmless.
- CoreHR workflow seeders (`WorkflowDefinitionCacheWriter`) previously wrote definitions under
  `corehr:qface:qimerp:workflow:definition:*`. They now write to the unprefixed key that
  Platform's publisher also writes, which is what Performance (unprefixed) already does. The
  old `corehr:` entries are orphaned and expire on their TTL.
- **Compile-time contract change.** The constructors of the eight classes above now take
  `ISharedDistributedCacheService` (or `ICacheService` → `ISharedDistributedCacheService` for
  the two integration clients). Consumer tests that construct them directly, or that replace
  `IDistributedCacheService` in DI with a fake, must also supply or replace
  `ISharedDistributedCacheService`. A fake only needs to add the marker interface. A factory
  that swaps only `IDistributedCacheService` still resolves the real `SharedRedisCacheService`,
  so the module gates in those tests silently read nothing.
- Hosts that register `TenantModuleAccessService` by hand, without `AddSharedServices`, must also
  register `ISharedDistributedCacheService` → `SharedRedisCacheService`. Today that is
  Core.Reporting `ReportingModuleAccessRegistration`.
- Shared.Common 3.11.48 depends on QFace.Sdk.RedisCache 1.0.4. Consumers that pin
  `QFace.Sdk.RedisCache` in `Directory.Packages.props` must raise it to 1.0.4 together with
  Shared.Common, and both nupkgs must be in their `local-nuget/`.

## Consumer follow-ups (service repos)

- Payroll `PayslipPdfService` and Learning `CertificateBrandingProvider` read
  `SharedCacheKeys.TenantBranding` through `IDistributedCacheService`. They work today only
  because those services have no prefix, so switch them to `ISharedDistributedCacheService`.
- Platform writers of shared keys should inject `ISharedDistributedCacheService`, so that
  adding a Platform prefix later cannot break the readers. There is no runtime change today.

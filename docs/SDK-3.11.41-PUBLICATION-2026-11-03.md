# SDK 3.11.41 publication — 3 November 2026

**Published and verified. All seven fresh CI-host restores passed.** This is the separately authorized publication follow-up to the earlier implementation phase.

## Published artifact

- [Public QimErp.Shared.Common 3.11.41 package](https://www.nuget.org/packages/QimErp.Shared.Common/3.11.41).
- [Successful publication run](https://github.com/qimerp/QFace.Sdk/actions/runs/41629166631).
- Release branch: `release/common-3.11.41-verified-20261103`; artifact/provenance commit [29e6dc3](https://github.com/qimerp/QFace.Sdk/commit/29e6dc37b79796ee8e8199dce72ef7629e93e7b3), OIDC commit [5c60a64](https://github.com/qimerp/QFace.Sdk/commit/5c60a64ddbe3e451276aaa59224394bdd5e47167).
- Uploaded SHA256: `32b1a1e413e065a2331c4df2921cc602a797578b15fd00d730efd9f9352be89e`.
- Public archive SHA256: `6e989d311431fbc7a29795b18679d609fdbc5f746c11c0fb5ca7f669b54890e8`.

All **six original ZIP entries have identical contents** in the published download. The only added entry is NuGet's `.signature.p7s`. The reviewed artifact was not rebuilt or repacked, and no other package was published.

The user-created trusted-publishing policy authenticates `iamqwame`, repository `qimerp/QFace.Sdk`, workflow `build.yml`, with no environment. The manual job uses `contents: read`, `id-token: write`, and official `NuGet/login` v1 pinned to `8d196754b4036150537f80ac539e15c2f1028841`. Its short-lived output goes through a step environment variable; there is no static-key fallback. This follows [NuGet's trusted-publishing procedure](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

## Seven actual clean restores

Verification ran **serially on the designated Contabo CI host**, `217.216.77.65`, Linux hostname `vmi3589242`, .NET **9.0.319**. No runtime host or runner configuration was changed.

The harness verified copied project/configuration hashes, generated the actual CI test-project solution graph, and executed:

```bash
dotnet restore "$CI_SLN" --configfile "$REPO/nuget.config" \
  --packages "$TASK_REPO/packages" --no-http-cache
```

Each repository began with absent package, HTTP-cache and scratch directories. Every generated asset listed only nuget.org; each downloaded Common package matched the public archive hash above. Task-owned caches were removed after verification, and the serial launcher exited successfully.

| Consumer | Result | Asset graphs |
| --- | --- | --- |
| Platform | PASS | 3 |
| CoreHr | PASS | 3 |
| Finance | PASS | 3 |
| Operations | PASS | 3 |
| Payroll | PASS | 3 |
| Learning | PASS | 3 |
| Performance | PASS | 3 |

**Seven fresh remote restores; 21 asset graphs.** Initial Mac attempts with interrupted large downloads were stopped and are not counted as passing evidence.

## Reviewed CI changes and limits

The exact **25 CI/configuration paths** are recorded in the linked manifest: seven lowercase remote-only `nuget.config` files, seven PR-validation workflows, seven WebApi Dockerfiles, three narrow gitignore exceptions (Operations/Learning/Performance), and CoreHr `Directory.Build.props`.

PR workflows explicitly restore their generated solution with the remote configuration and per-job package directory, then build without restore. Dockerfiles no longer require local package folders. Operations' case-insensitive configuration alias was safely canonicalized.

The first real CoreHr clean restore caught a hidden `RestoreAdditionalProjectSources` injection of `local-nuget`. Only that property group and its stale comment were removed; `UserSecretsId` and other properties were preserved. The subsequent fresh Linux restore passed without a fallback source or ignored-source flag.

Root and independent reviewer approved the publication workflow, OIDC amendment, consumer CI changes and additional CoreHr property fix. Root independently verified all seven result records, 21 assets, package hashes and cache-cleanup flags. Syntax and scoped diff checks passed; unrelated work and original SDK stashes were preserved.

**Consumer CI changes remain reviewable and uncommitted. The OIDC release workflow exists only on the isolated release branch; the default SDK workflow was not migrated or merged.** No consumer PR workflow run, new Docker image build, application deployment or new full application test run is claimed by this restore phase.

## SECURITY LOGGING, EVENT CLASSIFICATION & AUDIT TRAIL ASSESSMENT

The pinned official login masks OIDC and temporary API keys; no credentials were extracted into local files or logs. GitHub records the manual publication run, commit and provenance gate. Restore evidence contains dependency diagnostics, versions, paths and hashes, not application or tenant payloads. No application audit writer was added. Production monitoring/retention, compliance and a general vulnerability audit were outside this scoped publication review.

## Durable evidence

- [Publication verification and original-entry hashes](releases/common-3.11.41/publication-verification.json).
- [All seven CI-host results and exact commands](releases/common-3.11.41/ci-restore-results.json).
- [Twenty-five changed paths and before/current hashes](releases/common-3.11.41/ci-changed-file-manifest.json).
- [Input manifest hashes](releases/common-3.11.41/ci-input-manifest.json).
- Individual records and logs: `releases/common-3.11.41/ci-results/` and `releases/common-3.11.41/ci-logs/`.

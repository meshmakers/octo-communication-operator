using k8s.Models;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Helm;
using Meshmakers.Octo.Communication.Operator.Reconcilers;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

internal class DeployAsyncTests : WorkloadReconcilerTestsBase
{
    private static WorkloadDeployedDto BaseDto(IReadOnlyList<ValueOverrideDto>? overrides = null) => new()
    {
        TenantId = TenantId,
        PoolName = PoolName,
        WorkloadName = WorkloadName,
        WorkloadType = WorkloadTypeDto.Application,
        RepositoryUrl = "https://meshmakers.github.io/charts",
        ChartName = "voest-app",
        ChartVersion = "1.2.3",
        ValuesYaml = "image:\n  pullPolicy: IfNotPresent\n",
        Values = overrides ?? Array.Empty<ValueOverrideDto>(),
    };

    [Test]
    public async Task DeployAsync_NoSecrets_DoesNotCreateSecret()
    {
        await Reconciler.DeployAsync(BaseDto(), CancellationToken.None);

        await Gateway.DidNotReceiveWithAnyArgs().CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_NoSecrets_RemovesStaleSecretIfPresent()
    {
        Gateway.SecretExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Reconciler.DeployAsync(BaseDto(), CancellationToken.None);

        await Gateway.Received(1).DeleteSecretAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_WithSecrets_CreatesSecretWithMatchingKeys()
    {
        var dto = BaseDto(new[]
        {
            new ValueOverrideDto { Path = "image.tag", Value = "v1", IsSecret = false },
            new ValueOverrideDto { Path = "oauth.clientSecret", Value = "super-secret", IsSecret = true },
            new ValueOverrideDto { Path = "db.password", Value = "p@ss", IsSecret = true },
        });

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Gateway.Received(1).CreateSecretAsync(
            Arg.Any<string>(),
            Arg.Is<V1Secret>(s =>
                s.Data.Count == 2
                && s.Data.ContainsKey("oauth.clientSecret")
                && s.Data.ContainsKey("db.password")
                && s.Type == "Opaque"
                && s.Metadata.Labels["octo-mesh.meshmakers.io/tenant"] == TenantId
                && s.Metadata.Labels["octo-mesh.meshmakers.io/workload"] == WorkloadName),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_WithSecrets_ReplacesExistingSecret()
    {
        Gateway.SecretExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Reconciler.DeployAsync(BaseDto(new[]
        {
            new ValueOverrideDto { Path = "x", Value = "y", IsSecret = true },
        }), CancellationToken.None);

        await Gateway.Received(1).DeleteSecretAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await Gateway.Received(1).CreateSecretAsync(
            Arg.Any<string>(), Arg.Any<V1Secret>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_RegistersRepoAndRunsUpgrade()
    {
        await Reconciler.DeployAsync(BaseDto(), CancellationToken.None);

        await Helm.Received(1).EnsureRepoAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_ForwardsAuthFromRepository()
    {
        var dto = BaseDto() with
        {
            RepositoryUsername = "octo-bot",
            RepositoryPassword = "pat-abc",
        };

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Helm.Received(1).EnsureRepoAsync(
            Arg.Any<string>(),
            Arg.Is<string>(url => url == "https://meshmakers.github.io/charts"),
            Arg.Is<string?>(u => u == "octo-bot"),
            Arg.Is<string?>(p => p == "pat-abc"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_InvokesUpgradeWithCorrectReleaseAndChartRef()
    {
        await Reconciler.DeployAsync(BaseDto(), CancellationToken.None);

        var expectedRelease = WorkloadReconciler.ReleaseName(TenantId, WorkloadName);
        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Is<string>(r => r == expectedRelease),
            Arg.Is<string>(c => c.EndsWith("/voest-app")),
            Arg.Is<string>(v => v == "1.2.3"),
            Arg.Is<string>(n => n == PoolNamespace),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_PassesBaseAndOverrideValuesFiles()
    {
        // 2 files passed: base ValuesYaml + structured overrides.
        var dto = BaseDto(new[]
        {
            new ValueOverrideDto { Path = "image.tag", Value = "abc", IsSecret = false },
        });

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(files => files.Count == 2),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_EmptyValuesYaml_NoBaseFilePassed()
    {
        var dto = BaseDto() with { ValuesYaml = string.Empty };

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(files => files.Count == 0),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }
}

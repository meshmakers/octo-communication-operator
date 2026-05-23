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
        PoolRtId = PoolRtId, PoolName = PoolName,
        WorkloadRtId = WorkloadRtId, WorkloadName = WorkloadName,
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
                && s.Metadata.Labels["octo-mesh.meshmakers.io/pool-rt-id"] == PoolRtId
                && s.Metadata.Labels["octo-mesh.meshmakers.io/workload-rt-id"] == WorkloadRtId
                && s.Metadata.Annotations["octo-mesh.meshmakers.io/workload-name"] == WorkloadName
                && s.Metadata.Annotations["octo-mesh.meshmakers.io/pool-name"] == PoolName),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_WorkloadNameWithSpaces_LandsInAnnotationVerbatim()
    {
        // Workload names from the CK entity can contain spaces or
        // punctuation (e.g. "meshtest Adapter"). The K8s apiserver
        // used to reject those when used as a label value. Now the
        // workload's identity label is the WorkloadRtId (always
        // DNS-safe) and the user-facing name lands in an annotation
        // verbatim.
        var dto = BaseDto(new[]
        {
            new ValueOverrideDto { Path = "x", Value = "y", IsSecret = true },
        }) with { WorkloadName = "meshtest Adapter" };

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Gateway.Received(1).CreateSecretAsync(
            Arg.Any<string>(),
            Arg.Is<V1Secret>(s =>
                s.Metadata.Labels["octo-mesh.meshmakers.io/workload-rt-id"] == WorkloadRtId
                && s.Metadata.Annotations["octo-mesh.meshmakers.io/workload-name"] == "meshtest Adapter"),
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

        var expectedRelease = WorkloadReconciler.ReleaseName(TenantId, WorkloadRtId);
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
    public async Task DeployAsync_PassesContextBaseAndOverrideValuesFiles()
    {
        // 3 files passed: workload-identity context (tenantId is always set
        // on the DTO) + base ValuesYaml + structured overrides.
        var dto = BaseDto(new[]
        {
            new ValueOverrideDto { Path = "image.tag", Value = "abc", IsSecret = false },
        });

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(files => files.Count == 3),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_EmptyValuesYaml_OnlyContextFilePassed()
    {
        // ValuesYaml empty + no structured overrides → just the workload-
        // identity context file remains (tenantId is always present).
        var dto = BaseDto() with { ValuesYaml = string.Empty };

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(files =>
                files.Count == 1 && files[0].EndsWith("values-context.yaml")),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_WithWorkloadIdentity_AlwaysWritesContextFile()
    {
        // The context builder always emits a tenantId / adapterRtId block
        // when both are set, so a workload that has its identity (which
        // any real workload does) but no ValuesYaml and no cluster
        // context still gets a single values file passed to helm.
        var dto = BaseDto() with { ValuesYaml = string.Empty };

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(files => files.Count == 1),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_WithOperatorContext_PassesContextFileFirst()
    {
        Options.InstancePrefix = "test-2";
        Options.ClusterDependencies.MongodbHost = "mongo:27017";

        var dto = BaseDto(new[]
        {
            new ValueOverrideDto { Path = "image.tag", Value = "v1", IsSecret = false },
        });

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        // 3 files: context (operator defaults) + base ValuesYaml + structured overrides.
        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(files =>
                files.Count == 3
                && files[0].EndsWith("values-context.yaml")
                && files[1].EndsWith("values-base.yaml")
                && files[2].EndsWith("values-overrides.yaml")),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeployAsync_NoOperatorContextButHasWorkloadIdentity_StillEmitsContextFile()
    {
        // OperatorOptions in the base class are empty, but the DTO always
        // carries TenantId / WorkloadName — so the context layer is built
        // anyway and ordered before the base ValuesYaml.
        var dto = BaseDto();

        await Reconciler.DeployAsync(dto, CancellationToken.None);

        // 2 files: identity context + base ValuesYaml.
        await Helm.Received(1).UpgradeInstallAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<IReadOnlyList<string>>(files =>
                files.Count == 2
                && files[0].EndsWith("values-context.yaml")
                && files[1].EndsWith("values-base.yaml")),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<CancellationToken>());
    }
}

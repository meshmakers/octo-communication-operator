using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Reconcilers;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

internal class AppendClusterSecretsTests
{
    private static OperatorOptions FullClusterOptions() => new()
    {
        BrokerPassword = "rabbit-pwd",
        ClusterSecrets = new ClusterSecretsOptions
        {
            MongodbUserPassword = "mongo-user-pwd",
            MongodbAdminPassword = "mongo-admin-pwd",
            StreamDataPassword = "crate-pwd",
        },
    };

    [Test]
    public async Task FlagOff_BrokerPasswordSet_InjectsOnlyRabbitmq()
    {
        // Regression for pure edge adapters (Modbus / Loxone): the broker
        // password must be injected regardless of ReceivesClusterSecrets,
        // because every adapter needs the controller command bus. Data-store
        // secrets stay gated on the flag.
        var existing = new[] { new ValueOverrideDto { Path = "image.tag", Value = "v1", IsSecret = false } };

        var result = WorkloadReconciler.AppendClusterSecrets(existing, receivesClusterSecrets: false, FullClusterOptions());

        var injected = result.Where(e => e.IsSecret).ToArray();
        await Assert.That(injected.Length).IsEqualTo(1);
        await Assert.That(injected[0].Path).IsEqualTo("secrets.rabbitmq");
        await Assert.That(injected[0].Value).IsEqualTo("rabbit-pwd");
        // Mongo + CrateDB stay out when the flag is off.
        var paths = result.Select(e => e.Path).ToArray();
        await Assert.That(paths).DoesNotContain("secrets.databaseUser");
        await Assert.That(paths).DoesNotContain("secrets.databaseAdmin");
        await Assert.That(paths).DoesNotContain("secrets.streamDataPassword");
    }

    [Test]
    public async Task FlagOff_NoBrokerPassword_ReturnsOriginalListUnchanged()
    {
        var existing = new[] { new ValueOverrideDto { Path = "image.tag", Value = "v1", IsSecret = false } };

        var result = WorkloadReconciler.AppendClusterSecrets(existing, receivesClusterSecrets: false, new OperatorOptions());

        await Assert.That(result).IsSameReferenceAs(existing);
    }

    [Test]
    public async Task FlagOn_NoOptionsSet_ReturnsOriginalListUnchanged()
    {
        var existing = new[] { new ValueOverrideDto { Path = "image.tag", Value = "v1", IsSecret = false } };

        var result = WorkloadReconciler.AppendClusterSecrets(existing, receivesClusterSecrets: true, new OperatorOptions());

        await Assert.That(result).IsSameReferenceAs(existing);
    }

    [Test]
    public async Task FlagOn_FullOptions_InjectsAllFourSecretFlaggedEntries()
    {
        var result = WorkloadReconciler.AppendClusterSecrets(Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, FullClusterOptions());

        await Assert.That(result.Count).IsEqualTo(4);
        foreach (var entry in result)
        {
            await Assert.That(entry.IsSecret).IsTrue();
        }

        var paths = result.Select(e => e.Path).ToArray();
        await Assert.That(paths).Contains("secrets.databaseUser");
        await Assert.That(paths).Contains("secrets.databaseAdmin");
        await Assert.That(paths).Contains("secrets.streamDataPassword");
        await Assert.That(paths).Contains("secrets.rabbitmq");
    }

    [Test]
    public async Task FlagOn_PartialOptions_OnlyInjectsSetValues()
    {
        var opts = new OperatorOptions
        {
            BrokerPassword = "only-rabbit",
        };

        var result = WorkloadReconciler.AppendClusterSecrets(Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Path).IsEqualTo("secrets.rabbitmq");
        await Assert.That(result[0].Value).IsEqualTo("only-rabbit");
        await Assert.That(result[0].IsSecret).IsTrue();
    }

    [Test]
    public async Task FlagOn_EntityOverridesAreOrderedAfterInjected_SoEntityWinsOnSamePath()
    {
        // Entity-supplied override for secrets.databaseUser must take precedence
        // over the operator's injected value. WorkloadOverrideYamlBuilder's
        // last-write-wins rule means the entity entry must come after the
        // operator entry in the merged list.
        var entityOverride = new ValueOverrideDto { Path = "secrets.databaseUser", Value = "entity-pwd", IsSecret = true };

        var result = WorkloadReconciler.AppendClusterSecrets(new[] { entityOverride }, receivesClusterSecrets: true, FullClusterOptions());

        var index = result.Select((e, i) => new { e, i })
            .Where(x => x.e.Path == "secrets.databaseUser")
            .ToArray();
        await Assert.That(index.Length).IsEqualTo(2);
        // Operator-injected entry first, entity entry second.
        await Assert.That(index[0].e.Value).IsEqualTo("mongo-user-pwd");
        await Assert.That(index[1].e.Value).IsEqualTo("entity-pwd");
    }
}

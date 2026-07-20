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

    // --- AB#4417: root CA propagation to workload values ---
    //
    // The root CA the operator itself trusts (chart value `secrets.rootCa`)
    // must reach every deployed workload too, so a workload talking TLS to
    // the Communication Controller on a private-CA cluster (e.g. the kind
    // getting-started quickstart) can validate the connection. Unlike the
    // Tier-2 cluster secrets, this must NOT be gated on ReceivesClusterSecrets
    // — the simulation adapter has that flag false but still needs TLS trust
    // for its controller connection, exactly like the RabbitMQ password is
    // unconditional. Unlike the RabbitMQ password, the injected entry is
    // NOT secret-flagged: the workload chart's own `secrets.rootCa` handling
    // (templates/secret.yaml + deployment.yaml) requires a plain string so it
    // can `b64enc` it directly — a `valueFrom` map there would break chart
    // rendering.

    [Test]
    public async Task RootCaSet_ReceivesClusterSecretsFalse_InjectsPlainRootCaValue()
    {
        var opts = new OperatorOptions { RootCaCertificate = "ca-pem-content" };

        var result = WorkloadReconciler.AppendClusterSecrets(Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: false, opts);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Path).IsEqualTo("secrets.rootCa");
        await Assert.That(result[0].Value).IsEqualTo("ca-pem-content");
        await Assert.That(result[0].IsSecret).IsFalse();
    }

    [Test]
    public async Task RootCaSet_ReceivesClusterSecretsTrue_InjectsRootCaAlongsideGatedSecrets()
    {
        var opts = new OperatorOptions { RootCaCertificate = "ca-pem-content" };

        var result = WorkloadReconciler.AppendClusterSecrets(Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts);

        var paths = result.Select(e => e.Path).ToArray();
        await Assert.That(paths).Contains("secrets.rootCa");
        var rootCaEntry = result.Single(e => e.Path == "secrets.rootCa");
        await Assert.That(rootCaEntry.IsSecret).IsFalse();
        await Assert.That(rootCaEntry.Value).IsEqualTo("ca-pem-content");
    }

    [Test]
    public async Task RootCaNotSet_DoesNotInjectRootCaKey()
    {
        var existing = new[] { new ValueOverrideDto { Path = "image.tag", Value = "v1", IsSecret = false } };

        var resultFlagOff = WorkloadReconciler.AppendClusterSecrets(existing, receivesClusterSecrets: false, FullClusterOptions());
        var resultFlagOn = WorkloadReconciler.AppendClusterSecrets(existing, receivesClusterSecrets: true, FullClusterOptions());

        await Assert.That(resultFlagOff.Select(e => e.Path)).DoesNotContain("secrets.rootCa");
        await Assert.That(resultFlagOn.Select(e => e.Path)).DoesNotContain("secrets.rootCa");
    }

    [Test]
    public async Task RootCaSet_EntityOverrideOrderedAfterInjected_SoEntityWins()
    {
        var entityOverride = new ValueOverrideDto { Path = "secrets.rootCa", Value = "entity-ca-pem", IsSecret = false };
        var opts = new OperatorOptions { RootCaCertificate = "operator-ca-pem" };

        var result = WorkloadReconciler.AppendClusterSecrets(new[] { entityOverride }, receivesClusterSecrets: false, opts);

        var index = result.Select((e, i) => new { e, i })
            .Where(x => x.e.Path == "secrets.rootCa")
            .ToArray();
        await Assert.That(index.Length).IsEqualTo(2);
        await Assert.That(index[0].e.Value).IsEqualTo("operator-ca-pem");
        await Assert.That(index[1].e.Value).IsEqualTo("entity-ca-pem");
    }

    [Test]
    public async Task RootCaSet_GeneratedOverrideYaml_ContainsPlainValue_NotSecretKeyRef()
    {
        // Bridges into WorkloadOverrideYamlBuilder to prove the rendered
        // values file the workload chart actually reads carries a literal
        // string at secrets.rootCa, not a valueFrom.secretKeyRef envelope.
        var opts = new OperatorOptions { RootCaCertificate = "ca-pem-content" };

        var injected = WorkloadReconciler.AppendClusterSecrets(Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: false, opts);
        var yaml = WorkloadOverrideYamlBuilder.Build(injected, "rel-octo-secrets");

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"rootCa\": \"ca-pem-content\"");
        await Assert.That(yaml!).DoesNotContain("valueFrom");
    }
}

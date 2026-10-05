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
        var opts = FullClusterOptions();
        opts.RootCaCertificate = "ca-pem-content";

        var result = WorkloadReconciler.AppendClusterSecrets(Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts);

        var paths = result.Select(e => e.Path).ToArray();
        await Assert.That(paths).Contains("secrets.rootCa");
        await Assert.That(paths).Contains("secrets.databaseUser");

        var rootCaEntry = result.Single(e => e.Path == "secrets.rootCa");
        await Assert.That(rootCaEntry.IsSecret).IsFalse();
        await Assert.That(rootCaEntry.Value).IsEqualTo("ca-pem-content");

        var mongoEntry = result.Single(e => e.Path == "secrets.databaseUser");
        await Assert.That(mongoEntry.IsSecret).IsTrue();
        await Assert.That(mongoEntry.Value).IsEqualTo("mongo-user-pwd");
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

    // AB#5449 — the IronOCR licence key. Gated on the CHART, not on
    // ReceivesClusterSecrets: that opt-in means "this adapter talks to the cluster's
    // data stores", which says nothing about OCR. The four tests below pin both
    // directions of that gate, because an over-broad injection would materialise a
    // commercial licence key into the Secret of every Loxone / Modbus / Zenon pod.
    [Test]
    public async Task IronOcrLicense_ChartReadsIt_InjectsSecretFlaggedEntry()
    {
        var opts = new OperatorOptions { IronOcrLicenseKey = "IRONOCR.TEST.KEY" };

        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: false, opts, "octo-mesh-adapter");

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Path).IsEqualTo("secrets.ironOcrLicenseKey");
        await Assert.That(result[0].Value).IsEqualTo("IRONOCR.TEST.KEY");
        await Assert.That(result[0].IsSecret).IsTrue();
    }

    [Test]
    public async Task IronOcrLicense_ChartDoesNotReadIt_InjectsNothing()
    {
        var opts = new OperatorOptions { IronOcrLicenseKey = "IRONOCR.TEST.KEY" };

        // An edge adapter that never runs OCR must not carry the licence.
        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts, "octo-loxone-adapter");

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task IronOcrLicense_UnknownChartName_InjectsNothing()
    {
        // Null is what every pre-AB#5449 caller passes; it must stay inert rather
        // than defaulting to "inject everywhere".
        var opts = new OperatorOptions { IronOcrLicenseKey = "IRONOCR.TEST.KEY" };

        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task IronOcrLicense_NotConfigured_InjectsNothing()
    {
        // Unset is a supported state: most tenants never run OCR, and the adapter
        // fails on first OCR use rather than at startup.
        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: false,
            new OperatorOptions(), "octo-mesh-adapter");

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task IronOcrLicense_AlongsideClusterSecrets_AddsToThemRatherThanReplacing()
    {
        var opts = FullClusterOptions();
        opts.IronOcrLicenseKey = "IRONOCR.TEST.KEY";

        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts, "octo-mesh-adapter");

        await Assert.That(result.Count).IsEqualTo(5);
        var paths = result.Select(e => e.Path).ToArray();
        await Assert.That(paths).Contains("secrets.ironOcrLicenseKey");
        await Assert.That(paths).Contains("secrets.rabbitmq");
        await Assert.That(paths).Contains("secrets.databaseUser");
        foreach (var entry in result)
        {
            await Assert.That(entry.IsSecret).IsTrue();
        }
    }

    // --- Dash0 browser-RUM token -------------------------------------------------
    // Same chart gate as the OCR licence, and pinned in both directions for a sharper
    // reason: the NON-secret half (endpoint / dataset / environment) reaches every
    // workload through WorkloadContextValuesBuilder. A frontend chart missing from the
    // allowlist therefore comes up with an endpoint and no token and quietly decides
    // Dash0 is not activated — a failure with no error anywhere to notice.
    [Test]
    public async Task Dash0WebAuthToken_FrontendChart_InjectsSecretFlaggedEntry()
    {
        var opts = new OperatorOptions { Dash0WebAuthToken = "auth_test" };

        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: false, opts, "meshmakers-app");

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Path).IsEqualTo("secrets.dash0WebAuthToken");
        await Assert.That(result[0].Value).IsEqualTo("auth_test");
        await Assert.That(result[0].IsSecret).IsTrue();
    }

    [Test]
    public async Task Dash0WebAuthToken_BackendChart_InjectsNothing()
    {
        // A browser-RUM token is of no use to an adapter, whose server-side telemetry
        // already reaches Dash0 via the injected auto-instrumentation.
        var opts = new OperatorOptions { Dash0WebAuthToken = "auth_test" };

        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts, "octo-mesh-adapter");

        await Assert.That(result.Select(e => e.Path)).DoesNotContain("secrets.dash0WebAuthToken");
    }

    [Test]
    public async Task Dash0WebAuthToken_UnknownChartName_InjectsNothing()
    {
        var opts = new OperatorOptions { Dash0WebAuthToken = "auth_test" };

        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Dash0WebAuthToken_NotConfigured_InjectsNothing()
    {
        // The default state on a cluster without Dash0 activated, and on every cluster
        // before its Vault key exists.
        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: false,
            new OperatorOptions(), "meshmakers-app");

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Dash0WebAuthToken_EveryAllowlistedChart_IsInjected()
    {
        // Guards the list itself: a chart added to ChartsReadingDash0WebAuthToken without
        // a secrets.dash0WebAuthToken block in its values is the silent half of this
        // feature, so at minimum the operator side must agree with itself.
        var opts = new OperatorOptions { Dash0WebAuthToken = "auth_test" };

        foreach (var chart in WorkloadReconciler.ChartsReadingDash0WebAuthToken)
        {
            var result = WorkloadReconciler.AppendClusterSecrets(
                Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: false, opts, chart);

            await Assert.That(result.Select(e => e.Path)).Contains("secrets.dash0WebAuthToken");
        }
    }

    // --- AB#5536: SECRET attribute key ring --------------------------------------
    // Gated on ReceivesClusterSecrets like the data-store credentials: a workload that
    // talks to the cluster's MongoDB runs the runtime engine and reads/writes SECRET
    // attribute values. An edge adapter without that opt-in must not carry the key.
    private static OperatorOptions KeyRingOptions(string? activeKeyId = "k1") => new()
    {
        ClusterSecrets = new ClusterSecretsOptions
        {
            SecretEncryptionKeys = new Dictionary<string, string> { ["k1"] = "key-one" },
            SecretEncryptionActiveKeyId = activeKeyId,
            SecretEncryptionLegacyV1Key = "key-one",
        },
    };

    [Test]
    public async Task KeyRing_FlagOn_InjectsKeysActiveIdAndLegacyKey()
    {
        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, KeyRingOptions());

        var key = result.Single(e => e.Path == "secrets.secretEncryption.keys.k1");
        await Assert.That(key.Value).IsEqualTo("key-one");
        await Assert.That(key.IsSecret).IsTrue();

        var active = result.Single(e => e.Path == "secrets.secretEncryption.activeKeyId");
        await Assert.That(active.Value).IsEqualTo("k1");
        await Assert.That(active.IsSecret).IsFalse();

        var legacy = result.Single(e => e.Path == "secrets.secretEncryption.legacyV1Key");
        await Assert.That(legacy.Value).IsEqualTo("key-one");
        await Assert.That(legacy.IsSecret).IsTrue();
    }

    [Test]
    public async Task KeyRing_FlagOff_InjectsNothing()
    {
        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: false, KeyRingOptions());

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task KeyRing_NotConfigured_InjectsNothing()
    {
        // Every cluster before its operator chart carries instanceSecretKey.
        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, new OperatorOptions());

        await Assert.That(result.Select(e => e.Path).Any(p => p.StartsWith("secrets.secretEncryption"))).IsFalse();
    }

    [Test]
    public async Task KeyRing_Rotation_InjectsEveryKeyInStableOrder()
    {
        var opts = new OperatorOptions
        {
            ClusterSecrets = new ClusterSecretsOptions
            {
                SecretEncryptionKeys = new Dictionary<string, string> { ["k2"] = "key-two", ["k1"] = "key-one" },
                SecretEncryptionActiveKeyId = "k2",
                SecretEncryptionLegacyV1Key = "key-one",
            },
        };

        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts);

        var keyPaths = result.Select(e => e.Path).Where(p => p.StartsWith("secrets.secretEncryption.keys.")).ToArray();
        await Assert.That(keyPaths).IsEquivalentTo(new[] { "secrets.secretEncryption.keys.k1", "secrets.secretEncryption.keys.k2" });
        await Assert.That(keyPaths[0]).IsEqualTo("secrets.secretEncryption.keys.k1");
        await Assert.That(result.Single(e => e.Path == "secrets.secretEncryption.activeKeyId").Value).IsEqualTo("k2");
    }

    [Test]
    public async Task KeyRing_NoActiveIdWithSingleKey_UsesThatKey()
    {
        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, KeyRingOptions(activeKeyId: null));

        await Assert.That(result.Single(e => e.Path == "secrets.secretEncryption.activeKeyId").Value).IsEqualTo("k1");
    }

    [Test]
    public async Task KeyRing_EmptyKeyValue_IsSkipped()
    {
        var opts = KeyRingOptions();
        opts.ClusterSecrets.SecretEncryptionKeys["k2"] = "";

        var result = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, opts);

        await Assert.That(result.Select(e => e.Path)).DoesNotContain("secrets.secretEncryption.keys.k2");
    }

    [Test]
    public async Task KeyRing_GeneratedOverrideYaml_KeysAsSecretKeyRef_ActiveIdPlain()
    {
        var injected = WorkloadReconciler.AppendClusterSecrets(
            Array.Empty<ValueOverrideDto>(), receivesClusterSecrets: true, KeyRingOptions());
        var yaml = WorkloadOverrideYamlBuilder.Build(injected, "rel-octo-secrets");

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"key\": \"secrets.secretEncryption.keys.k1\"");
        await Assert.That(yaml!).Contains("\"activeKeyId\": \"k1\"");
        await Assert.That(yaml!).DoesNotContain("key-one");
    }
}

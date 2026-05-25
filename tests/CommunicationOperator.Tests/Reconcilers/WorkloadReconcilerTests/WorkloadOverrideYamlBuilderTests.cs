using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Operator.Reconcilers;

namespace Meshmakers.Octo.Communication.Operator.Tests.Reconcilers.WorkloadReconcilerTests;

// All assertions use the double-quoted form because the builder runs every string
// scalar — keys included — through QuotedStringEventEmitter. The quoting is what
// stops Go YAML (used by Helm) from coercing all-digit strings like the
// blueprint-seeded RtId 670000000000000000000002 into float64 ("6.7e+23"), which
// otherwise lands in the rendered Deployment env and crashes the adapter SDK with
// "not a valid 24 digit hex string". Quoted keys are valid YAML and Helm reads
// them identically to unquoted keys.
internal class WorkloadOverrideYamlBuilderTests
{
    [Test]
    public async Task Build_NoOverrides_ReturnsNull()
    {
        var yaml = WorkloadOverrideYamlBuilder.Build(Array.Empty<ValueOverrideDto>(), "my-release-octo-secrets");
        await Assert.That(yaml).IsNull();
    }

    [Test]
    public async Task Build_PlainValue_EmitsLiteral()
    {
        var yaml = WorkloadOverrideYamlBuilder.Build(new[]
        {
            new ValueOverrideDto { Path = "image.tag", Value = "1.2.3", IsSecret = false },
        }, "x-octo-secrets");

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"image\":");
        await Assert.That(yaml!).Contains("\"tag\": \"1.2.3\"");
    }

    [Test]
    public async Task Build_AllDigitValue_IsQuotedToPreventFloatCoercion()
    {
        // Regression test for the bug where 670000000000000000000002 (24 decimal
        // digits) survived our serializer as a plain scalar, then Helm's Go YAML
        // re-read it as float64 → "6.7e+23" → adapter SDK could not parse it as a
        // 24-char hex ObjectId and the pod never registered with the controller.
        var yaml = WorkloadOverrideYamlBuilder.Build(new[]
        {
            new ValueOverrideDto { Path = "adapterRtId", Value = "670000000000000000000002", IsSecret = false },
        }, "rel-octo-secrets");

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"adapterRtId\": \"670000000000000000000002\"");
        await Assert.That(yaml!).DoesNotContain("6.7e+23");
    }

    [Test]
    public async Task Build_SecretValue_EmitsSecretKeyRefAtPath()
    {
        var yaml = WorkloadOverrideYamlBuilder.Build(new[]
        {
            new ValueOverrideDto { Path = "oauth.clientSecret", Value = "ignored-here", IsSecret = true },
        }, "rel-octo-secrets");

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"oauth\":");
        await Assert.That(yaml!).Contains("\"clientSecret\":");
        await Assert.That(yaml!).Contains("\"valueFrom\":");
        await Assert.That(yaml!).Contains("\"secretKeyRef\":");
        await Assert.That(yaml!).Contains("\"name\": \"rel-octo-secrets\"");
        await Assert.That(yaml!).Contains("\"key\": \"oauth.clientSecret\"");
        // Plaintext must not appear.
        await Assert.That(yaml!).DoesNotContain("ignored-here");
    }

    [Test]
    public async Task Build_MixedSecretAndPlain_BothAppear()
    {
        var yaml = WorkloadOverrideYamlBuilder.Build(new[]
        {
            new ValueOverrideDto { Path = "image.tag", Value = "v9", IsSecret = false },
            new ValueOverrideDto { Path = "oauth.clientSecret", Value = "secret-value", IsSecret = true },
        }, "rel-octo-secrets");

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"tag\": \"v9\"");
        await Assert.That(yaml!).Contains("\"secretKeyRef\":");
    }

    [Test]
    public async Task Build_DeepPath_BuildsNestedDictionary()
    {
        var yaml = WorkloadOverrideYamlBuilder.Build(new[]
        {
            new ValueOverrideDto { Path = "a.b.c.d", Value = "leaf", IsSecret = false },
        }, "rel-octo-secrets");

        await Assert.That(yaml).IsNotNull();
        await Assert.That(yaml!).Contains("\"a\":");
        await Assert.That(yaml!).Contains("\"b\":");
        await Assert.That(yaml!).Contains("\"c\":");
        await Assert.That(yaml!).Contains("\"d\": \"leaf\"");
    }
}

using Meshmakers.Octo.Communication.Operator.Common;

namespace Meshmakers.Octo.Communication.Operator.Tests.Common;

public class DictionaryExtensionsTests
{
    [Test]
    public async Task AsLabelSelector_EmptyDictionary_ReturnsEmptyString()
    {
        var result = new Dictionary<string, string>().AsLabelSelector();

        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task AsLabelSelector_SingleEntry_ReturnsKeyValuePair()
    {
        var labels = new Dictionary<string, string>
        {
            ["app"] = "operator"
        };

        var result = labels.AsLabelSelector();

        await Assert.That(result).IsEqualTo("app=operator");
    }

    [Test]
    public async Task AsLabelSelector_MultipleEntries_JoinsWithComma()
    {
        var labels = new Dictionary<string, string>
        {
            ["app"] = "operator",
            ["tenant"] = "acme"
        };

        var result = labels.AsLabelSelector();

        await Assert.That(result).IsEqualTo("app=operator,tenant=acme");
    }
}

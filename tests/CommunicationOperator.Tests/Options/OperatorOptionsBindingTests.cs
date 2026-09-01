using Meshmakers.Octo.Communication.Operator.Options;
using Microsoft.Extensions.Configuration;

namespace Meshmakers.Octo.Communication.Operator.Tests.Options;

public class OperatorOptionsBindingTests
{
    // Program.cs binds the "Operator" section of the host configuration, which reads
    // environment variables with no prefix - so the property name below is the deployment
    // contract, spelled OPERATOR__AUTHURI in every cluster values file. Renaming the
    // property breaks that contract without a build error and without a startup failure:
    // the section still binds and the value simply stays null, which the adapter then sees
    // as an unconfigured authority.
    [Test]
    [NotInParallel]
    public async Task AuthUri_BindsFromTheDocumentedEnvironmentVariable()
    {
        const string variable = "OPERATOR__AUTHURI";
        const string authority = "https://connect.test-2.mm.cloud";

        Environment.SetEnvironmentVariable(variable, authority);
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            var options = configuration.GetSection("Operator").Get<OperatorOptions>();

            await Assert.That(options).IsNotNull();
            await Assert.That(options!.AuthUri).IsEqualTo(authority);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // AB#5062 - same contract, for the credential the operator authenticates /operatorHub with.
    // These four variable names are what a cluster values file writes; a rename here silently
    // leaves the operator unconfigured, which means anonymous, which means refused as soon as the
    // controller-side gate (AB#5059) is armed.
    [Test]
    [NotInParallel]
    public async Task Authentication_BindsFromTheDocumentedEnvironmentVariables()
    {
        var variables = new Dictionary<string, string>
        {
            ["OPERATOR__AUTHENTICATION__ISSUERURI"] = "https://connect.test-2.mm.cloud",
            ["OPERATOR__AUTHENTICATION__CLIENTID"] = "octo-communication-operator",
            ["OPERATOR__AUTHENTICATION__CLIENTSECRET"] = "a-secret",
            ["OPERATOR__AUTHENTICATION__TENANTID"] = "OctoSystem"
        };

        foreach (var variable in variables)
        {
            Environment.SetEnvironmentVariable(variable.Key, variable.Value);
        }

        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            var options = configuration.GetSection("Operator").Get<OperatorOptions>();

            await Assert.That(options).IsNotNull();
            await Assert.That(options!.Authentication.IssuerUri).IsEqualTo("https://connect.test-2.mm.cloud");
            await Assert.That(options.Authentication.ClientId).IsEqualTo("octo-communication-operator");
            await Assert.That(options.Authentication.ClientSecret).IsEqualTo("a-secret");
            await Assert.That(options.Authentication.TenantId).IsEqualTo("OctoSystem");
            await Assert.That(options.Authentication.IsEnabled).IsTrue();
        }
        finally
        {
            foreach (var variable in variables.Keys)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }
        }
    }

    // The default shape is the compatibility guarantee: an operator whose values file predates
    // AB#5062 binds an Authentication block that reports itself disabled, and the operator then
    // connects without a token exactly as it always has.
    [Test]
    [NotInParallel]
    public async Task Authentication_DefaultsToDisabled()
    {
        var options = new ConfigurationBuilder().Build().GetSection("Operator").Get<OperatorOptions>()
                      ?? new OperatorOptions();

        await Assert.That(options.Authentication).IsNotNull();
        await Assert.That(options.Authentication.IsEnabled).IsFalse();
    }
}

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
}

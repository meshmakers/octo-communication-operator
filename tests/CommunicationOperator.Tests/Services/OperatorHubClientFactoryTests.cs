using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Services;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.Authentication;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services;

/// <summary>
///     AB#5062 — the hub client must be handed the process-wide access token
///     <see cref="OperatorAccessTokenService" /> refreshes, not a private empty one.
/// </summary>
public class OperatorHubClientFactoryTests
{
    [Test]
    public async Task Create_HandsTheHubClientTheSharedAccessToken()
    {
        // The SDK reads this exact instance on every (re)connect. A per-client copy — which the
        // factory used to construct — can never be reached by a refresh, so the connection would
        // stay anonymous forever no matter how the operator is configured.
        var sharedAccessToken = new ServiceClientAccessToken { AccessToken = "operator-token" };
        var factory = new OperatorHubClientFactory(NullLoggerFactory.Instance, sharedAccessToken);

        var client = factory.Create(new OperatorHubClientOptions { EndpointUri = "https://localhost:5015" },
            Substitute.For<IOperatorHubCallbacks>());

        await Assert.That(client.ClientAccessToken).IsSameReferenceAs(sharedAccessToken);
        await Assert.That(client.ClientAccessToken.AccessToken).IsEqualTo("operator-token");
    }

    [Test]
    public async Task Create_ForwardsALaterRefreshToTheHubClient()
    {
        var sharedAccessToken = new ServiceClientAccessToken();
        var factory = new OperatorHubClientFactory(NullLoggerFactory.Instance, sharedAccessToken);
        var client = factory.Create(new OperatorHubClientOptions { EndpointUri = "https://localhost:5015" },
            Substitute.For<IOperatorHubCallbacks>());

        sharedAccessToken.AccessToken = "refreshed-token";

        await Assert.That(client.ClientAccessToken.AccessToken).IsEqualTo("refreshed-token");
    }
}

/// <summary>
///     AB#5062 / AB#5058 — pins the projection of the operator's configuration onto the SDK
///     authenticator, above all the tenant that ends up in <c>acr_values</c>.
/// </summary>
public class ConfigureOperatorAuthenticatorOptionsTests
{
    private static AuthenticatorOptions Project(OperatorAuthenticationOptions authentication)
    {
        var configurator = new ConfigureOperatorAuthenticatorOptions(
            Microsoft.Extensions.Options.Options.Create(new OperatorOptions { Authentication = authentication }));
        var options = new AuthenticatorOptions();
        configurator.Configure(options);
        return options;
    }

    [Test]
    public async Task ProjectsEveryCredentialField_IncludingTheTenant()
    {
        var options = Project(new OperatorAuthenticationOptions
        {
            IssuerUri = "https://connect.test-2.mm.cloud",
            ClientId = "octo-communication-operator",
            ClientSecret = "secret",
            TenantId = "OctoSystem"
        });

        await Assert.That(options.IssuerUri).IsEqualTo("https://connect.test-2.mm.cloud");
        await Assert.That(options.ClientId).IsEqualTo("octo-communication-operator");
        await Assert.That(options.ClientSecret).IsEqualTo("secret");
        // Without this, AuthenticatorClient omits acr_values and the identity service refuses the
        // request outright once the client id is mirrored (AB#5058).
        await Assert.That(options.TenantId).IsEqualTo("OctoSystem");
    }

    [Test]
    public async Task UnconfiguredOperator_YieldsBlankRatherThanNullAuthorityFields()
    {
        // AuthorizationClient only builds its discovery cache for a non-blank IssuerUri; a null
        // there would still be blank, but ArgumentValidation elsewhere in the SDK is happier with
        // an empty string, and the service never calls the client in this state anyway.
        var options = Project(new OperatorAuthenticationOptions());

        await Assert.That(options.IssuerUri).IsEqualTo(string.Empty);
        await Assert.That(options.ClientId).IsEqualTo(string.Empty);
        await Assert.That(options.ClientSecret).IsNull();
        await Assert.That(options.TenantId).IsNull();
    }
}

using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Communication.Operator.Services;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.Tenants;
using Meshmakers.Octo.Sdk.ServiceClient.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Communication.Operator.Tests.Services;

/// <summary>
///     AB#5062 — the operator must present a real access token on <c>/operatorHub</c> when it is
///     given credentials, and must behave exactly as it always has when it is not.
/// </summary>
public class OperatorAccessTokenServiceTests
{
    private const string IssuerUri = "https://connect.test-2.mm.cloud";
    private const string ClientId = "octo-communication-operator";

    private static OperatorAccessTokenService CreateService(
        IAuthenticatorClient authenticatorClient,
        IServiceClientAccessToken accessToken,
        OperatorAuthenticationOptions? authentication = null) =>
        new(
            NullLogger<OperatorAccessTokenService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new OperatorOptions
            {
                Authentication = authentication ?? new OperatorAuthenticationOptions
                {
                    IssuerUri = IssuerUri,
                    ClientId = ClientId,
                    ClientSecret = "secret",
                    TenantId = "OctoSystem"
                }
            }),
            accessToken,
            authenticatorClient);

    private static void ReturnsToken(IAuthenticatorClient authenticatorClient, string token, TimeSpan lifetime)
    {
        // AuthenticatorClient computes ExpiresAt from DateTime.Now, so the fake mirrors that kind —
        // the service is expected to convert via the remaining lifetime rather than compare kinds.
        authenticatorClient.RequestClientCredentialsTokenAsync(
                Arg.Any<ApiScopes>(), Arg.Any<DefaultScopes>(), Arg.Any<IEnumerable<string>?>(),
                Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new AuthenticationData { AccessToken = token, ExpiresAt = DateTime.Now + lifetime });
    }

    [Test]
    public async Task ConfiguredOperator_PublishesTheTokenIntoTheSharedAccessToken()
    {
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        ReturnsToken(authenticatorClient, "operator-token", TimeSpan.FromHours(1));
        var accessToken = new ServiceClientAccessToken();
        using var service = CreateService(authenticatorClient, accessToken);

        var acquired = await service.EnsureTokenAsync();

        await Assert.That(acquired).IsTrue();
        await Assert.That(accessToken.AccessToken).IsEqualTo("operator-token");
    }

    [Test]
    public async Task ConfiguredOperator_RequestsOctoApiFullAccessWithoutOfflineAccess()
    {
        // octo_api is what the controller's SystemCommunicationApiPolicy requires; offline_access
        // would ask for a refresh token a client-credentials grant does not issue.
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        ReturnsToken(authenticatorClient, "operator-token", TimeSpan.FromHours(1));
        using var service = CreateService(authenticatorClient, new ServiceClientAccessToken());

        await service.EnsureTokenAsync();

        await authenticatorClient.Received(1).RequestClientCredentialsTokenAsync(
            ApiScopes.OctoApiFullAccess, DefaultScopes.None, Arg.Any<IEnumerable<string>?>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task UnconfiguredOperator_NeverRequestsAToken_AndConnectsAnonymously()
    {
        // The compatibility guarantee: every operator in the estate runs without these keys today,
        // and must keep connecting exactly as before.
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        var accessToken = new ServiceClientAccessToken();
        using var service = CreateService(authenticatorClient, accessToken, new OperatorAuthenticationOptions());

        var acquired = await service.EnsureTokenAsync();

        await Assert.That(acquired).IsFalse();
        await Assert.That(accessToken.AccessToken).IsNull();
        await authenticatorClient.DidNotReceiveWithAnyArgs().RequestClientCredentialsTokenAsync(
            default, default, default, default, default);
    }

    [Test]
    public async Task IssuerUriWithoutClientId_CountsAsUnconfigured()
    {
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        using var service = CreateService(authenticatorClient, new ServiceClientAccessToken(),
            new OperatorAuthenticationOptions { IssuerUri = IssuerUri });

        await Assert.That(await service.EnsureTokenAsync()).IsFalse();
        await authenticatorClient.DidNotReceiveWithAnyArgs().RequestClientCredentialsTokenAsync(
            default, default, default, default, default);
    }

    [Test]
    public async Task AValidTokenIsNotReAcquired()
    {
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        ReturnsToken(authenticatorClient, "operator-token", TimeSpan.FromHours(1));
        using var service = CreateService(authenticatorClient, new ServiceClientAccessToken());

        await service.EnsureTokenAsync();
        await service.EnsureTokenAsync();

        await authenticatorClient.Received(1).RequestClientCredentialsTokenAsync(
            Arg.Any<ApiScopes>(), Arg.Any<DefaultScopes>(), Arg.Any<IEnumerable<string>?>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task ATokenInsideTheRefreshWindowIsReplaced()
    {
        // The reconnect is the exposure: an operator that keeps a near-expired token would present
        // it on the next reconnect and be refused for good once the gate enforces.
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        var accessToken = new ServiceClientAccessToken();
        using var service = CreateService(authenticatorClient, accessToken);

        ReturnsToken(authenticatorClient, "first-token",
            OperatorAccessTokenService.RefreshSkew - TimeSpan.FromMinutes(1));
        await service.EnsureTokenAsync();
        await Assert.That(accessToken.AccessToken).IsEqualTo("first-token");

        ReturnsToken(authenticatorClient, "second-token", TimeSpan.FromHours(1));
        await service.EnsureTokenAsync();

        await Assert.That(accessToken.AccessToken).IsEqualTo("second-token");
    }

    [Test]
    public async Task AFailedAcquisitionKeepsThePreviousTokenAndDoesNotThrow()
    {
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        var accessToken = new ServiceClientAccessToken();
        using var service = CreateService(authenticatorClient, accessToken);

        ReturnsToken(authenticatorClient, "first-token",
            OperatorAccessTokenService.RefreshSkew - TimeSpan.FromMinutes(1));
        await service.EnsureTokenAsync();

        authenticatorClient.RequestClientCredentialsTokenAsync(
                Arg.Any<ApiScopes>(), Arg.Any<DefaultScopes>(), Arg.Any<IEnumerable<string>?>(),
                Arg.Any<string?>(), Arg.Any<string?>())
            .ThrowsAsync(new HttpRequestException("identity service unreachable"));

        var acquired = await service.EnsureTokenAsync();

        await Assert.That(acquired).IsFalse();
        await Assert.That(accessToken.AccessToken).IsEqualTo("first-token");
    }

    [Test]
    public async Task ATokenlessResponseIsNotPublished()
    {
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        authenticatorClient.RequestClientCredentialsTokenAsync(
                Arg.Any<ApiScopes>(), Arg.Any<DefaultScopes>(), Arg.Any<IEnumerable<string>?>(),
                Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(new AuthenticationData { AccessToken = null, ExpiresAt = DateTime.Now.AddHours(1) });
        var accessToken = new ServiceClientAccessToken();
        using var service = CreateService(authenticatorClient, accessToken);

        var acquired = await service.EnsureTokenAsync();

        await Assert.That(acquired).IsFalse();
        await Assert.That(accessToken.AccessToken).IsNull();
    }

    [Test]
    public async Task StartAsync_AcquiresTheTokenBeforeItReturns()
    {
        // Hosted services start sequentially, so completing acquisition inside StartAsync is what
        // guarantees the first hub connection already carries a token instead of racing it.
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        ReturnsToken(authenticatorClient, "operator-token", TimeSpan.FromHours(1));
        var accessToken = new ServiceClientAccessToken();
        using var service = CreateService(authenticatorClient, accessToken);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Assert.That(accessToken.AccessToken).IsEqualTo("operator-token");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task StartAsync_OnAnUnconfiguredOperator_StartsCleanlyWithoutAToken()
    {
        var authenticatorClient = Substitute.For<IAuthenticatorClient>();
        var accessToken = new ServiceClientAccessToken();
        using var service = CreateService(authenticatorClient, accessToken, new OperatorAuthenticationOptions());

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Assert.That(accessToken.AccessToken).IsNull();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}

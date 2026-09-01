using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.ServiceClient.Authentication;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
///     Keeps the operator's own service credential — the access token every <c>/operatorHub</c>
///     connection presents — current for the lifetime of the process (AB#5062).
/// </summary>
/// <remarks>
///     <para>
///         The token is written into the process-wide <see cref="IServiceClientAccessToken" /> that
///         <see cref="OperatorHubClientFactory" /> hands to the SignalR client. The SDK reads that
///         object through <c>HttpConnectionOptions.AccessTokenProvider</c> on <b>every</b> connection
///         attempt, so a token refreshed here is picked up by the next (re)connect without anything
///         having to notify the client.
///     </para>
///     <para>
///         <b>Why a refresh loop is needed even though the connection survives expiry.</b> An
///         established SignalR connection is authorized once, at connect time: the controller's
///         <c>OperatorHubAuthorizationFilter</c> runs in <c>OnConnectedAsync</c> and the service does
///         not set <c>HubOptions.CloseOnAuthenticationExpiration</c>, so a live connection is not torn
///         down when the bearer expires. The exposure is the <b>re</b>connect — and an operator
///         reconnects routinely (controller rollout, node drain, network blip, the SDK watchdog). An
///         operator that acquired one token at startup would reconnect days later with a long-expired
///         one and, under <c>Enforce</c>, be refused permanently: pools unregistered, no workload
///         deploys, and no self-healing path short of a pod restart. The loop closes that.
///     </para>
///     <para>
///         Acquisition happens in <see cref="StartAsync" />, before the base class starts the loop.
///         Hosted services are started sequentially, so registering this service before
///         <see cref="OperatorHubService" /> means the first hub connection already carries a token
///         instead of racing the first acquisition.
///     </para>
///     <para>
///         Every failure is logged and swallowed. Refusing to start would turn a temporarily
///         unreachable identity service into an operator outage, and the connection itself is still
///         valuable while the controller-side gate observes rather than enforces.
///     </para>
/// </remarks>
internal sealed class OperatorAccessTokenService : BackgroundService
{
    /// <summary>
    ///     How long before its own expiry a token is replaced. Comfortably longer than a token
    ///     request plus a reconnect, so a connection attempt never picks up a token that dies in
    ///     flight.
    /// </summary>
    internal static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Floor for the sleep between refresh attempts. Also the cadence after a failed acquisition,
    ///     so a controller that came up before the identity service recovers on its own.
    /// </summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceClientAccessToken _accessToken;
    private readonly IAuthenticatorClient _authenticatorClient;
    private readonly ILogger<OperatorAccessTokenService> _logger;
    private readonly OperatorAuthenticationOptions _options;

    private DateTime _expiresAtUtc = DateTime.MinValue;

    public OperatorAccessTokenService(
        ILogger<OperatorAccessTokenService> logger,
        IOptions<OperatorOptions> options,
        IServiceClientAccessToken accessToken,
        IAuthenticatorClient authenticatorClient)
    {
        _logger = logger;
        _options = options.Value.Authentication;
        _accessToken = accessToken;
        _authenticatorClient = authenticatorClient;
    }

    /// <summary>Test seam: the delay between refresh attempts is driven at millisecond speed.</summary>
    internal TimeSpan RetryIntervalOverride { get; set; } = RetryInterval;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsEnabled)
        {
            // One warning, at startup, naming the consequence rather than the missing key — this is
            // the line an operator reads when the controller-side inventory (AB#5059 LogOnly) shows
            // their operator as anonymous.
            _logger.LogWarning(
                "No Operator:Authentication:ClientId / IssuerUri configured. The operator connects to the " +
                "communication controller's /operatorHub without an access token, exactly as before. The " +
                "controller's operator-hub authorization (AB#5059) must stay in LogOnly for this installation.");
            await base.StartAsync(cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.TenantId))
        {
            _logger.LogWarning(
                "Operator:Authentication:ClientId '{ClientId}' is configured without a TenantId. The token " +
                "request carries no acr_values, which the identity service refuses outright once the client " +
                "id is mirrored into child tenants (AB#5058). Set Operator:Authentication:TenantId to the " +
                "tenant the operator's client is registered in - normally the system tenant.",
                _options.ClientId);
        }

        await EnsureTokenAsync();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NextDelay(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await EnsureTokenAsync();
        }
    }

    /// <summary>
    ///     Time to sleep before the next attempt: until shortly before the current token expires, or
    ///     the retry cadence when there is no usable token (never acquired, or the last attempt
    ///     failed). Never shorter than the retry cadence, so a pathologically short-lived token
    ///     cannot turn this into a hot loop against the identity service.
    /// </summary>
    private TimeSpan NextDelay()
    {
        var untilRefresh = _expiresAtUtc - RefreshSkew - DateTime.UtcNow;
        return untilRefresh > RetryIntervalOverride ? untilRefresh : RetryIntervalOverride;
    }

    /// <summary>
    ///     Acquires a token unless the current one is still comfortably valid. Returns whether a
    ///     usable token is in place afterwards.
    /// </summary>
    internal async Task<bool> EnsureTokenAsync()
    {
        if (!_options.IsEnabled)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(_accessToken.AccessToken) && _expiresAtUtc > DateTime.UtcNow + RefreshSkew)
        {
            return true;
        }

        try
        {
            // DefaultScopes.None: exactly "octo_api", which is what the controller's
            // SystemCommunicationApiPolicy requires. Notably WITHOUT offline_access - a
            // client-credentials grant has no refresh token by design, and re-running the grant is
            // both cheaper and the only way a revoked client stops being able to connect.
            // acr_values=tenant:{TenantId} is appended by AuthenticatorClient from the configured
            // AuthenticatorOptions.TenantId (AB#5058); see OperatorAuthenticationOptions.TenantId.
            var authenticationData = await _authenticatorClient.RequestClientCredentialsTokenAsync(
                ApiScopes.OctoApiFullAccess, DefaultScopes.None);

            if (string.IsNullOrWhiteSpace(authenticationData.AccessToken))
            {
                _logger.LogError(
                    "The identity service at {IssuerUri} accepted the operator's client-credentials request " +
                    "for client {ClientId} but returned no access token",
                    _options.IssuerUri, _options.ClientId);
                return false;
            }

            // AuthenticatorClient builds ExpiresAt from DateTime.Now (local kind). Deriving the
            // remaining lifetime and re-basing it on UtcNow keeps this correct regardless of the
            // kind the SDK happens to use - comparing the two directly would be off by the local
            // offset, which on a CEST cluster means acting on a token two hours after it died.
            var lifetime = authenticationData.ExpiresAt - DateTime.Now;
            _expiresAtUtc = DateTime.UtcNow + (lifetime > TimeSpan.Zero ? lifetime : TimeSpan.Zero);
            _accessToken.AccessToken = authenticationData.AccessToken;

            _logger.LogInformation(
                "Operator access token acquired for client {ClientId} in tenant '{TenantId}', expires at {ExpiresAtUtc:O}",
                _options.ClientId, _options.TenantId ?? string.Empty, _expiresAtUtc);
            return true;
        }
        catch (Exception e)
        {
            // The previously acquired token is deliberately left in place. It is no less usable than
            // no token at all, and dropping it would guarantee a refusal on the next reconnect for a
            // failure that is usually a transient identity-service blip.
            _logger.LogError(e,
                "Could not acquire an access token for client {ClientId} from {IssuerUri}; the operator hub " +
                "connection keeps using the previous token (if any) and the next attempt runs in {RetryInterval}",
                _options.ClientId, _options.IssuerUri, RetryIntervalOverride);
            return false;
        }
    }
}

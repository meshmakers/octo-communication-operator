using Meshmakers.Octo.Communication.Operator.Options;
using Meshmakers.Octo.Sdk.ServiceClient.Authentication;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Communication.Operator.Services;

/// <summary>
///     Projects <see cref="OperatorAuthenticationOptions" /> onto the SDK's
///     <see cref="AuthenticatorOptions" />, which is what <see cref="AuthenticatorClient" /> reads
///     (AB#5062).
/// </summary>
/// <remarks>
///     An <see cref="IConfigureOptions{TOptions}" /> rather than an inline delegate in
///     <c>Program.cs</c> so the projection can be pinned by a test. The field that makes that worth
///     doing is <see cref="OperatorAuthenticationOptions.TenantId" />: dropping it produces a token
///     request without <c>acr_values</c>, which the identity service answers with a plain
///     <c>invalid_request</c> as soon as the client id is mirrored (AB#5058) — a failure that looks
///     like an outage and points nowhere near this mapping.
/// </remarks>
internal sealed class ConfigureOperatorAuthenticatorOptions : IConfigureOptions<AuthenticatorOptions>
{
    private readonly OperatorAuthenticationOptions _authentication;

    public ConfigureOperatorAuthenticatorOptions(IOptions<OperatorOptions> operatorOptions)
    {
        _authentication = operatorOptions.Value.Authentication;
    }

    public void Configure(AuthenticatorOptions options)
    {
        // Empty rather than null: AuthorizationClient builds its discovery cache only for a
        // non-blank IssuerUri, so an unconfigured operator still constructs the client without
        // throwing — and OperatorAccessTokenService never calls it.
        options.IssuerUri = _authentication.IssuerUri ?? string.Empty;
        options.ClientId = _authentication.ClientId ?? string.Empty;
        options.ClientSecret = _authentication.ClientSecret;
        // Drives acr_values=tenant:{TenantId} on the token request (AB#5058).
        options.TenantId = _authentication.TenantId;
    }
}

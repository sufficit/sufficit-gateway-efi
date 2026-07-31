using Sufficit.Gateway;

namespace Sufficit.Gateway.Efi.Tests;

internal sealed class StaticGatewayCredentialResolver : IGatewayCredentialResolver
{
    public Task<GatewayCredential> GetRequiredAsync(
        string providerCode,
        GatewayCallContext context,
        CancellationToken cancellationToken)
        => Task.FromResult(
            string.Equals(providerCode, "efi", StringComparison.OrdinalIgnoreCase)
                ? new GatewayCredential
                {
                    ClientId = "efi-client",
                    ClientSecret = "efi-secret"
                }
                : new GatewayCredential
                {
                    ApiKey = "$aact_hmlg_test"
                });
}

using Sufficit.Finance;

namespace Sufficit.Gateway.Efi.Tests;

internal sealed class StaticBankSlipCredentialResolver : IBankSlipCredentialResolver
{
    public Task<BankSlipProviderCredential> GetRequiredAsync(
        string providerCode,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
        => Task.FromResult(
            string.Equals(providerCode, BankSlipProviderCodes.Efi, StringComparison.OrdinalIgnoreCase)
                ? new BankSlipProviderCredential
                {
                    ClientId = "efi-client",
                    ClientSecret = "efi-secret"
                }
                : new BankSlipProviderCredential
                {
                    ApiKey = "$aact_hmlg_test"
                });
}

namespace Sufficit.Gateway.Efi;

/// <summary>
/// Configures the Efí Billing API client.
/// </summary>
public sealed class EfiBankSlipGatewayOptions
{
    public const string SectionName = "BankSlips:Providers:Efi";
    public Uri SandboxBaseAddress { get; set; } = new("https://cobrancas-h.api.efipay.com.br/");
    public Uri ProductionBaseAddress { get; set; } = new("https://cobrancas.api.efipay.com.br/");
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan TokenClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

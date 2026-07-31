namespace Sufficit.Gateway.Efi;

/// <summary>
/// Configures the Efí API client shared by all provider capabilities.
/// </summary>
public sealed class EfiGatewayOptions
{
    public const string SectionName = "Sufficit:Gateway:Efi";
    public Uri BillingSandboxBaseAddress { get; set; } = new("https://cobrancas-h.api.efipay.com.br/");
    public Uri BillingProductionBaseAddress { get; set; } = new("https://cobrancas.api.efipay.com.br/");
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan TokenClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

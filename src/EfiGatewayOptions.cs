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

    /// <summary>
    /// Default used when a caller does not explicitly decide whether the payer
    /// e-mail should be included. Efí may send collection messages directly to
    /// the payer when this is enabled, so the safe default is false.
    /// </summary>
    public bool IncludePayerEmail { get; set; }
}

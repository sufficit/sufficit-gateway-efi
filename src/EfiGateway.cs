using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sufficit.Gateway;

namespace Sufficit.Gateway.Efi;

/// <summary>
/// Shared Efí provider façade. Product capabilities are implemented in
/// separate partial files and reuse this provider-level dependency boundary.
/// </summary>
public sealed partial class EfiGateway
{
    public const string HttpClientName = "Sufficit.Gateway.Efi";
    public const string ProviderCodeValue = "efi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IGatewayCredentialResolver _credentialResolver;
    private readonly IOptionsMonitor<EfiGatewayOptions> _options;
    private readonly ILogger<EfiGateway> _logger;

    public EfiGateway(
        IHttpClientFactory httpClientFactory,
        IGatewayCredentialResolver credentialResolver,
        IOptionsMonitor<EfiGatewayOptions> options,
        ILogger<EfiGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credentialResolver = credentialResolver;
        _options = options;
        _logger = logger;
    }
}

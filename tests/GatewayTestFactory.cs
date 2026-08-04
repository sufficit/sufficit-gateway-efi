using Microsoft.Extensions.DependencyInjection;
using Sufficit.Finance;
using Sufficit.Gateway;

namespace Sufficit.Gateway.Efi.Tests;

internal static class GatewayTestFactory
{
    public static EfiGateway CreateEfi(
        RecordingHttpMessageHandler handler,
        Action<EfiGatewayOptions>? configure = null)
    {
        var services = CreateServices();
        services.Configure<EfiGatewayOptions>(options =>
        {
            options.Timeout = TimeSpan.FromSeconds(5);
            options.TokenClockSkew = TimeSpan.Zero;
            configure?.Invoke(options);
        });
        services.AddHttpClient(EfiGateway.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<EfiGateway>();
        return services.BuildServiceProvider().GetRequiredService<EfiGateway>();
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGatewayCredentialResolver, StaticGatewayCredentialResolver>();
        return services;
    }
}

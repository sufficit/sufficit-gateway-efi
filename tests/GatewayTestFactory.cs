using Microsoft.Extensions.DependencyInjection;
using Sufficit.Finance;

namespace Sufficit.Gateway.Efi.Tests;

internal static class GatewayTestFactory
{
    public static EfiBankSlipGateway CreateEfi(RecordingHttpMessageHandler handler)
    {
        var services = CreateServices();
        services.Configure<EfiBankSlipGatewayOptions>(options =>
        {
            options.Timeout = TimeSpan.FromSeconds(5);
            options.TokenClockSkew = TimeSpan.Zero;
        });
        services.AddHttpClient(EfiBankSlipGateway.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<EfiBankSlipGateway>();
        return services.BuildServiceProvider().GetRequiredService<EfiBankSlipGateway>();
    }

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBankSlipCredentialResolver, StaticBankSlipCredentialResolver>();
        return services;
    }
}

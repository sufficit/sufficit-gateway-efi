using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sufficit.Finance;
using Sufficit.Gateway;

namespace Sufficit.Gateway.Efi;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSufficitGatewayEfi(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<EfiGatewayOptions>()
            .Bind(configuration.GetSection(EfiGatewayOptions.SectionName));

        services.AddHttpClient(EfiGateway.HttpClientName);

        services.TryAddSingleton<EfiGateway>();
        services.AddSingleton<IBankSlipGateway>(
            serviceProvider => serviceProvider.GetRequiredService<EfiGateway>());
        services.AddSingleton<IBankSlipProviderDiagnosticsGateway>(
            serviceProvider => serviceProvider.GetRequiredService<EfiGateway>());
        services.AddSingleton<IBankSlipProviderNotificationGateway>(
            serviceProvider => serviceProvider.GetRequiredService<EfiGateway>());
        services.AddSingleton<IGatewayDiagnosticsGateway>(
            serviceProvider => serviceProvider.GetRequiredService<EfiGateway>());

        return services;
    }
}

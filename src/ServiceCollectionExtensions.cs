using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sufficit.Finance;

namespace Sufficit.Gateway.Efi;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSufficitEfiBankSlipGateway(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<EfiBankSlipGatewayOptions>()
            .Bind(configuration.GetSection(EfiBankSlipGatewayOptions.SectionName));

        services.AddHttpClient(EfiBankSlipGateway.HttpClientName);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBankSlipGateway, EfiBankSlipGateway>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IBankSlipProviderDiagnosticsGateway, EfiBankSlipGateway>());

        return services;
    }
}

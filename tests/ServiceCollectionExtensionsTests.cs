using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Sufficit.Finance;
using Sufficit.Gateway;
using Xunit;

namespace Sufficit.Gateway.Efi.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void RegistrationExposesOneProviderFacadeThroughBankSlipCapabilities()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGatewayCredentialResolver, StaticGatewayCredentialResolver>();
        services.AddSufficitGatewayEfi(configuration);

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<EfiGateway>();

        Assert.Same(gateway, provider.GetRequiredService<IBankSlipGateway>());
        Assert.Same(gateway, provider.GetRequiredService<IBankSlipProviderNotificationGateway>());
        Assert.Same(gateway, provider.GetRequiredService<IBankSlipProviderDiagnosticsGateway>());
        Assert.Same(gateway, provider.GetRequiredService<IGatewayDiagnosticsGateway>());
    }
}

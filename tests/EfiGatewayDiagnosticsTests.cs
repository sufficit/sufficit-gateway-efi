using Sufficit.Gateway;
using Xunit;

namespace Sufficit.Gateway.Efi.Tests;

public sealed class EfiGatewayDiagnosticsTests
{
    [Fact]
    public async Task ChargeQueryIsAllowListedAndUsesSandboxOAuth()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""{"code":200,"data":{"charge_id":12345,"status":"waiting"}}""");
        IGatewayDiagnosticsGateway gateway = GatewayTestFactory.CreateEfi(handler);

        var result = await gateway.ExecuteDiagnosticAsync(
            new GatewayDiagnosticRequest
            {
                Provider = "efi",
                OperationCode = "charges.get",
                ResourceId = "12345",
                Limit = 20
            },
            CreateContext(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://cobrancas-h.api.efipay.com.br/v1/charge/12345",
            handler.Requests[1].Uri.AbsoluteUri);
        Assert.Contains(gateway.DiagnosticOperations, item =>
            item.Code == "pix.transactions" && !item.Available);
    }

    [Theory]
    [InlineData("charges.list-boleto", "billet")]
    [InlineData("charges.list-card", "card")]
    public async Task ChargeListsUseEfiChargeCategoryEnums(
        string operationCode,
        string chargeType)
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""{"code":200,"data":[]}""");
        IGatewayDiagnosticsGateway gateway = GatewayTestFactory.CreateEfi(handler);

        var result = await gateway.ExecuteDiagnosticAsync(
            new GatewayDiagnosticRequest
            {
                Provider = "efi",
                OperationCode = operationCode,
                Limit = 20
            },
            CreateContext(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(
            $"/v1/charges?charge_type={chargeType}&",
            handler.Requests[1].Uri.PathAndQuery,
            StringComparison.Ordinal);
    }

    private static GatewayCallContext CreateContext()
        => new()
        {
            TenantId = OSInformation.SufficitId,
            Environment = GatewayEnvironment.Sandbox,
            CredentialReference = "tests/efi"
        };
}

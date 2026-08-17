using Sufficit.Finance;
using Xunit;

namespace Sufficit.Gateway.Efi.Tests;

public sealed class EfiGatewayInventoryTests
{
    [Fact]
    public async Task InventoryUsesBoundedBoletoQueryAndReturnsPiiFreeFacts()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""
            {
              "code": 200,
              "data": [
                {
                  "id": 711008222,
                  "total": 5000,
                  "status": "paid",
                  "custom_id": "41490624a468419fb7bf359b0d619ee1",
                  "created_at": "2026-08-10 20:23:31",
                  "customer": { "name": "must not cross the boundary", "cpf": "00000000000" },
                  "payment": { "paid_at": "2026-08-11T10:00:00.000Z", "paid_value": 5000 }
                }
              ]
            }
            """);
        IBankSlipProviderInventoryGateway gateway = GatewayTestFactory.CreateEfi(handler);

        var result = await gateway.GetInventoryAsync(
            new ProviderBankSlipInventoryRequest
            {
                FromDate = new DateTime(2026, 8, 10),
                ToDate = new DateTime(2026, 8, 16),
                MaximumItems = 500
            },
            new BankSlipGatewayContext
            {
                TenantId = OSInformation.SufficitId,
                Environment = BankSlipProviderEnvironment.Sandbox,
                CredentialReference = "tests/efi"
            },
            CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("711008222", item.ChargeId);
        Assert.Equal("41490624a468419fb7bf359b0d619ee1", item.CustomId);
        Assert.Equal(BankSlipStatus.Paid, item.Status);
        Assert.Equal(50m, item.Value);
        Assert.Equal(50m, item.PaidValue);
        Assert.Equal(new DateTime(2026, 8, 10, 23, 23, 31, DateTimeKind.Utc), item.CreatedAtUtc);
        Assert.Equal(1, result.RequestCount);
        Assert.False(result.Truncated);
        Assert.Equal(
            "https://cobrancas-h.api.efipay.com.br/v1/charges?charge_type=billet&begin_date=2026-08-10&end_date=2026-08-16&limit=100&offset=0",
            handler.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task InventoryRejectsPeriodsLongerThanThirtyOneCalendarDays()
    {
        IBankSlipProviderInventoryGateway gateway = GatewayTestFactory.CreateEfi(
            new RecordingHttpMessageHandler());

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetInventoryAsync(
            new ProviderBankSlipInventoryRequest
            {
                FromDate = new DateTime(2026, 7, 1),
                ToDate = new DateTime(2026, 8, 2)
            },
            new BankSlipGatewayContext
            {
                TenantId = OSInformation.SufficitId,
                Environment = BankSlipProviderEnvironment.Sandbox,
                CredentialReference = "tests/efi"
            },
            CancellationToken.None));
    }
}

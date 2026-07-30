using Sufficit.Finance;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Sufficit.Gateway.Efi.Tests;

public class EfiBankSlipGatewayTests
{
    [Fact]
    public async Task CreateAsyncUsesSandboxOAuthAndTwoStepFlow()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""{"code":200,"data":{"charge_id":12345,"status":"new"}}""");
        handler.EnqueueJson(
            """{"code":200,"data":{"charge_id":12345,"status":"waiting","barcode":"0019000009","link":"https://sandbox.efi.example/billet/12345"}}""");
        var gateway = GatewayTestFactory.CreateEfi(handler);
        var request = CreateIssueRequest();

        var result = await gateway.CreateAsync(request, CreateContext(), CancellationToken.None);

        Assert.Equal(BankSlipStatus.Ready, result.Status);
        Assert.Equal("12345", result.ChargeId);
        Assert.Equal("0019000009", result.BarCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("https://cobrancas-h.api.efipay.com.br/v1/authorize", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("https://cobrancas-h.api.efipay.com.br/v1/charge", handler.Requests[1].Uri.AbsoluteUri);
        Assert.Equal("https://cobrancas-h.api.efipay.com.br/v1/charge/12345/pay", handler.Requests[2].Uri.AbsoluteUri);
        Assert.Contains("\"custom_id\":\"8c732677a5ea4f33a8e13dfcdb538411\"", handler.Requests[1].Body);
        Assert.Contains("\"juridical_person\"", handler.Requests[2].Body);
        Assert.StartsWith("Basic ", handler.Requests[0].Headers["Authorization"].Single());
        Assert.Equal("Bearer token-123", handler.Requests[1].Headers["Authorization"].Single());
    }

    [Fact]
    public async Task CancelAsyncUsesOriginalEfiCharge()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""{"code":200,"data":{"charge_id":12345,"status":"canceled"}}""");
        var gateway = GatewayTestFactory.CreateEfi(handler);

        var result = await gateway.CancelAsync(
            "12345",
            CreateContext(),
            CancellationToken.None);

        Assert.True(result.Canceled);
        Assert.Equal("12345", result.ChargeId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Put, handler.Requests[1].Method);
        Assert.Equal(
            "https://cobrancas-h.api.efipay.com.br/v1/charge/12345/cancel",
            handler.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task CreateAsyncWithPersistedChargeReusesChargeForPayStep()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson(
            """{"code":200,"data":{"charge_id":12345,"status":"waiting","barcode":"0019000009","link":"https://sandbox.efi.example/billet/12345"}}""");
        var gateway = GatewayTestFactory.CreateEfi(handler);
        var request = CreateIssueRequest();
        request.ProviderChargeId = "12345";

        var result = await gateway.CreateAsync(
            request,
            CreateContext(),
            CancellationToken.None);

        Assert.Equal("12345", result.ChargeId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(
            handler.Requests,
            recorded => recorded.Uri.AbsoluteUri == "https://cobrancas-h.api.efipay.com.br/v1/charge");
        Assert.Equal(
            "https://cobrancas-h.api.efipay.com.br/v1/charge/12345/pay",
            handler.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task CreateAsyncRateLimitBeforeChargeIsTreatedAsAmbiguous()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson(
            """{"code":"rate_limit","message":"too many requests"}""",
            HttpStatusCode.TooManyRequests);
        var gateway = GatewayTestFactory.CreateEfi(handler);

        var exception = await Assert.ThrowsAsync<BankSlipGatewayException>(
            () => gateway.CreateAsync(
                CreateIssueRequest(),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(BankSlipErrorCategory.AmbiguousResult, exception.Category);
        Assert.Null(exception.ProviderChargeId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CreateAsyncPreservesSanitizedNestedValidationDetail()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""{"code":200,"data":{"charge_id":12345,"status":"new"}}""");
        handler.EnqueueJson(
            """
            {
              "code": 3500034,
              "error": "validation_error",
              "error_description": {
                "property": "/payment/banking_billet/customer/address",
                "message": "A propriedade [number] é obrigatória."
              }
            }
            """,
            HttpStatusCode.BadRequest);
        var gateway = GatewayTestFactory.CreateEfi(handler);

        var exception = await Assert.ThrowsAsync<BankSlipGatewayException>(
            () => gateway.CreateAsync(
                CreateIssueRequest(),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(BankSlipErrorCategory.Validation, exception.Category);
        Assert.Equal("3500034", exception.ErrorCode);
        Assert.Equal("validation_error", exception.ErrorName);
        Assert.Equal("A Efí recusou os dados da cobrança", exception.ErrorTitle);
        Assert.Contains("Retomar emissão", exception.ErrorAction, StringComparison.Ordinal);
        Assert.Equal("12345", exception.ProviderChargeId);
        Assert.Contains(
            "/payment/banking_billet/customer/address",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "A propriedade [number] é obrigatória.",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsyncRejectsIncompleteAddressBeforeCreatingEfiCharge()
    {
        var handler = new RecordingHttpMessageHandler();
        var gateway = GatewayTestFactory.CreateEfi(handler);
        var request = CreateIssueRequest();
        request.Payer.Address!.Number = string.Empty;

        var exception = await Assert.ThrowsAsync<BankSlipGatewayException>(
            () => gateway.CreateAsync(
                request,
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(BankSlipErrorCategory.Validation, exception.Category);
        Assert.Equal("efi_payer_address_number_missing", exception.ErrorCode);
        Assert.Equal("O número do endereço não foi informado", exception.ErrorTitle);
        Assert.Contains("cadastro", exception.ErrorAction, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateAsyncMapsIdenticalChargeLimitAsSecurityBlockWithoutRetryAdvice()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""{"code":200,"data":{"charge_id":12345,"status":"new"}}""");
        handler.EnqueueJson(
            """
            {
              "code": 4600210,
              "error": "validation_error",
              "error_description": "Não é possível emitir mais de três cobranças idênticas."
            }
            """,
            HttpStatusCode.BadRequest);
        var gateway = GatewayTestFactory.CreateEfi(handler);

        var exception = await Assert.ThrowsAsync<BankSlipGatewayException>(
            () => gateway.CreateAsync(
                CreateIssueRequest(),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(BankSlipErrorCategory.SecurityBlock, exception.Category);
        Assert.Equal("4600210", exception.ErrorCode);
        Assert.Equal("O limite de cobranças idênticas foi atingido", exception.ErrorTitle);
        Assert.StartsWith("Não tente novamente", exception.ErrorAction, StringComparison.Ordinal);
        Assert.Equal("12345", exception.ProviderChargeId);
    }

    [Theory]
    [InlineData("3500000")]
    [InlineData("4699999")]
    [InlineData("4999999")]
    public async Task CreateAsyncKeepsProviderAndUnknownFailuresAmbiguous(string code)
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""{"code":200,"data":{"charge_id":12345,"status":"new"}}""");
        handler.EnqueueJson(
            $$"""{"code":{{code}},"error":"provider_error","error_description":"Falha não conclusiva."}""",
            HttpStatusCode.BadRequest);
        var gateway = GatewayTestFactory.CreateEfi(handler);

        var exception = await Assert.ThrowsAsync<BankSlipGatewayException>(
            () => gateway.CreateAsync(
                CreateIssueRequest(),
                CreateContext(),
                CancellationToken.None));

        Assert.Equal(BankSlipErrorCategory.AmbiguousResult, exception.Category);
        Assert.Equal(code, exception.ErrorCode);
        Assert.Equal("12345", exception.ProviderChargeId);
    }

    [Fact]
    public async Task CreateAsyncNormalizesBrazilCountryCodeInPhonePayload()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600,"token_type":"Bearer"}""");
        handler.EnqueueJson("""{"code":200,"data":{"charge_id":12345,"status":"new"}}""");
        handler.EnqueueJson(
            """{"code":200,"data":{"charge_id":12345,"status":"waiting","barcode":"0019000009","link":"https://sandbox.efi.example/billet/12345"}}""");
        var gateway = GatewayTestFactory.CreateEfi(handler);
        var request = CreateIssueRequest();
        request.Payer.Phone = "5531999999999";

        await gateway.CreateAsync(request, CreateContext(), CancellationToken.None);

        Assert.Contains("\"phone_number\":\"31999999999\"", handler.Requests[2].Body);
        Assert.DoesNotContain("\"phone_number\":\"5531999999999\"", handler.Requests[2].Body);
    }

    [Fact]
    public async Task DiagnosticChargeReturnsRawProviderPayloadUsingReadOnlyRequest()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600}""");
        handler.EnqueueJson(
            """{"code":200,"data":{"charge_id":12345,"status":"waiting","history":[{"message":"created"}]}}""");
        var gateway = GatewayTestFactory.CreateEfi(handler);

        var result = await gateway.ExecuteDiagnosticAsync(
            new BankSlipProviderDiagnosticParameters
            {
                Provider = BankSlipProviderCodes.Efi,
                Operation = BankSlipProviderDiagnosticOperation.Charge,
                ProviderChargeId = "12345"
            },
            CreateContext(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(12345, result.Payload.GetProperty("data").GetProperty("charge_id").GetInt32());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Equal(
            "https://cobrancas-h.api.efipay.com.br/v1/charge/12345",
            handler.Requests[1].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task DiagnosticAuthenticationNeverReturnsAccessToken()
    {
        var handler = new RecordingHttpMessageHandler();
        handler.EnqueueJson("""{"access_token":"token-123","expires_in":600}""");
        var gateway = GatewayTestFactory.CreateEfi(handler);

        var result = await gateway.ExecuteDiagnosticAsync(
            new BankSlipProviderDiagnosticParameters
            {
                Provider = BankSlipProviderCodes.Efi,
                Operation = BankSlipProviderDiagnosticOperation.Authentication
            },
            CreateContext(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result.Payload.GetProperty("authenticated").GetBoolean());
        Assert.DoesNotContain("token-123", result.Payload.GetRawText(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    private static BankSlipGatewayIssueRequest CreateIssueRequest()
        => new()
        {
            BankSlipId = Guid.Parse("8c732677-a5ea-4f33-a8e1-3dfcdb538411"),
            ContextId = Guid.Parse("d9f76c63-e026-489b-9fd2-e3f5210dd8ac"),
            Value = 500m,
            Expiration = new DateTime(2026, 8, 10),
            Description = "Serviço Sufficit",
            NotificationUrl = new Uri("https://example.test/v2/Gateway/Efi/Notification"),
            Payer = new BankSlipPayerSnapshot
            {
                Document = "12.345.678/0001-90",
                Name = "Sufficit Cliente Ltda",
                CorporateName = "Sufficit Cliente Ltda",
                Email = "financeiro@example.test",
                Phone = "31999999999",
                Address = new BankSlipPayerAddress
                {
                    Street = "Rua de Teste",
                    Number = "100",
                    Neighborhood = "Centro",
                    PostalCode = "30100-000",
                    City = "Belo Horizonte",
                    State = "MG"
                }
            }
        };

    private static BankSlipGatewayContext CreateContext()
        => new()
        {
            TenantId = OSInformation.SufficitId,
            Environment = BankSlipProviderEnvironment.Sandbox,
            CredentialReference = "tests/efi"
        };
}

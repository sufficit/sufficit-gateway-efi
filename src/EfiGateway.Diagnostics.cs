using Sufficit.Finance;
using Sufficit.Gateway;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Sufficit.Gateway.Efi;

/// <summary>
/// Controlled, provider-wide Efí diagnostics. Billing and Pix capabilities are
/// represented separately because Pix requires its own scopes and mTLS setup.
/// </summary>
public sealed partial class EfiGateway : IGatewayDiagnosticsGateway
{
    private static readonly IReadOnlyList<GatewayDiagnosticOperation> Operations =
        new GatewayDiagnosticOperation[]
        {
            Operation("authentication", "Conta", "Validar autenticação", "Solicita um token novo para validar client ID e client secret."),
            Operation("charges.get", "Cobranças", "Consultar cobrança", "Consulta boleto ou cobrança de cartão pelo charge_id.", requiresResourceId: true),
            Operation("charges.list-boleto", "Boleto", "Listar boletos", "Lista cobranças de boleto criadas nos últimos 30 dias."),
            Operation("charges.list-card", "Cartão", "Listar cobranças de cartão", "Lista cobranças de cartão criadas nos últimos 30 dias."),
            Operation("notifications.get", "Notificações", "Consultar notificação", "Consulta o histórico associado ao token de notificação.", requiresResourceId: true),
            Unavailable("bank-slip.create", "Boleto", "Criar boleto", "POST", GatewayDiagnosticRisk.SandboxMutation,
                "A emissão permanece no fluxo de boletos, com idempotência e validação do pagador."),
            Unavailable("credit-card.tokenize", "Cartão", "Tokenizar cartão", "POST", GatewayDiagnosticRisk.Sensitive,
                "A Efí exige payment_token; dados brutos de cartão não podem transitar pelo Endpoints."),
            Unavailable("credit-card.pay", "Cartão", "Pagar com cartão tokenizado", "POST", GatewayDiagnosticRisk.SandboxMutation,
                "Será habilitado após a tokenização client-side e o contrato de confirmação do laboratório."),
            Unavailable("carnets.manage", "Carnês", "Gerenciar carnês", "POST", GatewayDiagnosticRisk.SandboxMutation,
                "O catálogo está preparado; faltam os contratos tipados de criação e alteração de parcelas."),
            Unavailable("subscriptions.manage", "Assinaturas", "Gerenciar assinaturas", "POST", GatewayDiagnosticRisk.SandboxMutation,
                "O catálogo está preparado; faltam os contratos tipados de planos e assinaturas."),
            Unavailable("payment-links.manage", "Links de pagamento", "Gerenciar links", "POST", GatewayDiagnosticRisk.SandboxMutation,
                "O catálogo está preparado; faltam os contratos tipados de links de pagamento."),
            Unavailable("pix.charges", "Pix", "Cobranças Pix", "GET", GatewayDiagnosticRisk.ReadOnly,
                "Requer credenciais Pix, escopos cob/cobv e certificado cliente mTLS."),
            Unavailable("pix.transactions", "Pix", "Transações Pix", "GET", GatewayDiagnosticRisk.ReadOnly,
                "Requer credenciais Pix, escopo pix.read e certificado cliente mTLS."),
            Unavailable("pix.webhooks", "Pix", "Webhooks Pix", "GET", GatewayDiagnosticRisk.ReadOnly,
                "Requer credenciais Pix, escopo webhook.read e certificado cliente mTLS."),
            Unavailable("pix.send", "Pix", "Enviar Pix", "PUT", GatewayDiagnosticRisk.ProductionMutation,
                "Requer pix.send, webhook da chave pagadora, mTLS, idempotência e confirmação reforçada.")
        };

    IReadOnlyList<GatewayDiagnosticOperation> IGatewayDiagnosticsGateway.DiagnosticOperations
        => Operations;

    async Task<GatewayDiagnosticProviderResult?> IGatewayDiagnosticsGateway.ExecuteDiagnosticAsync(
        GatewayDiagnosticRequest request,
        GatewayCallContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operation = Operations.FirstOrDefault(item =>
            string.Equals(item.Code, request.OperationCode, StringComparison.OrdinalIgnoreCase));
        if (operation == null || !operation.Available)
        {
            throw new InvalidOperationException("The requested Efí gateway operation is not executable.");
        }

        var bankSlipContext = ToBankSlipContext(context);
        if (operation.Code == "authentication")
        {
            _ = await GetAccessTokenAsync(
                bankSlipContext,
                forceRefresh: true,
                cancellationToken).ConfigureAwait(false);
            return new GatewayDiagnosticProviderResult
            {
                HttpStatusCode = (int)HttpStatusCode.OK,
                Payload = JsonSerializer.SerializeToElement(new
                {
                    authenticated = true,
                    credentialValidated = true
                })
            };
        }

        var resourceId = operation.RequiresResourceId
            ? RequireDiagnosticResourceId(request.ResourceId)
            : null;
        var relativePath = operation.Code switch
        {
            "charges.get" => $"v1/charge/{Uri.EscapeDataString(resourceId!)}",
            "charges.list-boleto" => BuildChargeListPath("billet"),
            "charges.list-card" => BuildChargeListPath("card"),
            "notifications.get" => $"v1/notification/{Uri.EscapeDataString(resourceId!)}",
            _ => throw new InvalidOperationException("The requested Efí gateway operation is not mapped.")
        };

        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                BuildUri(bankSlipContext, relativePath)),
            bankSlipContext,
            BankSlipOperation.Query,
            resourceId,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new GatewayDiagnosticProviderResult
        {
            HttpStatusCode = (int)response.StatusCode,
            Payload = document.RootElement.Clone()
        };
    }

    private static string BuildChargeListPath(string chargeType)
    {
        var end = DateTime.UtcNow.Date;
        var begin = end.AddDays(-30);
        return string.Format(
            CultureInfo.InvariantCulture,
            "v1/charges?charge_type={0}&begin_date={1:yyyy-MM-dd}&end_date={2:yyyy-MM-dd}",
            Uri.EscapeDataString(chargeType),
            begin,
            end);
    }

    private static BankSlipGatewayContext ToBankSlipContext(GatewayCallContext context)
        => new()
        {
            TenantId = context.TenantId,
            Environment = context.Environment == GatewayEnvironment.Production
                ? BankSlipProviderEnvironment.Production
                : BankSlipProviderEnvironment.Sandbox,
            CredentialReference = context.CredentialReference
        };

    private static GatewayDiagnosticOperation Operation(
        string code,
        string category,
        string title,
        string description,
        bool requiresResourceId = false)
        => new()
        {
            Code = code,
            Category = category,
            Title = title,
            Description = description,
            RequiresResourceId = requiresResourceId
        };

    private static GatewayDiagnosticOperation Unavailable(
        string code,
        string category,
        string title,
        string method,
        GatewayDiagnosticRisk risk,
        string note)
        => new()
        {
            Code = code,
            Category = category,
            Title = title,
            Description = note,
            Method = method,
            Risk = risk,
            Available = false,
            AvailabilityNote = note
        };

    private static string RequireDiagnosticResourceId(string? resourceId)
    {
        var value = resourceId?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            throw new ArgumentException("A valid Efí resource identifier is required.", nameof(resourceId));
        }

        return value;
    }
}

using Microsoft.Extensions.Logging;
using Sufficit.Finance;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Sufficit.Gateway.Efi;

/// <summary>
/// Read-only inventory capability used by administrative reconciliation.
/// The provider response is reduced to PII-free financial facts at this boundary.
/// </summary>
public sealed partial class EfiGateway : IBankSlipProviderInventoryGateway
{
    private const int InventoryPageSize = 100;
    private const int InventoryMaximumItems = 5000;

    public async Task<ProviderBankSlipInventoryResult> GetInventoryAsync(
        ProviderBankSlipInventoryRequest request,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fromDate = request.FromDate.Date;
        var toDate = request.ToDate.Date;
        if (toDate < fromDate)
            throw new ArgumentException("The inventory end date must not precede its start date.", nameof(request));
        if ((toDate - fromDate).TotalDays > 30)
            throw new ArgumentException("Efí inventory queries are limited to 31 calendar days.", nameof(request));

        var maximumItems = Math.Clamp(request.MaximumItems, 1, InventoryMaximumItems);
        var items = new List<ProviderBankSlipInventoryItem>(Math.Min(maximumItems, InventoryPageSize));
        var seenChargeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestCount = 0;
        var truncated = false;
        var partial = false;
        string? warningCode = null;
        string? warningMessage = null;

        for (var pageIndex = 0; pageIndex * InventoryPageSize < maximumItems; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageNumber = pageIndex + 1;
            var limit = Math.Min(InventoryPageSize, maximumItems - items.Count);
            var path = BuildInventoryPath(fromDate, toDate, limit, pageNumber);
            try
            {
                requestCount++;
                using var response = await SendAuthorizedAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, BuildUri(context, path)),
                    context,
                    BankSlipOperation.Query,
                    null,
                    cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    break;

                await EnsureSuccessAsync(
                    response,
                    BankSlipOperation.Query,
                    null,
                    cancellationToken).ConfigureAwait(false);
                using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                var page = ParseInventoryPage(document.RootElement);
                var novelItems = page
                    .Where(item => seenChargeIds.Add(item.ChargeId))
                    .ToArray();

                if (page.Count > 0 && novelItems.Length == 0)
                {
                    partial = true;
                    warningCode = "repeated_page";
                    warningMessage = "A EFI repetiu uma página já processada. A consulta foi interrompida para evitar chamadas duplicadas; os itens anteriores foram preservados.";
                    _logger.LogWarning(
                        "EFI inventory page {PageNumber} repeated previously loaded charge identifiers; returning {ItemCount} partial items after {RequestCount} requests.",
                        pageNumber,
                        items.Count,
                        requestCount);
                    break;
                }

                items.AddRange(novelItems);
                if (page.Count < limit)
                    break;

                if (items.Count >= maximumItems)
                {
                    truncated = true;
                    break;
                }
            }
            catch (Exception exception) when (
                items.Count > 0
                && !cancellationToken.IsCancellationRequested
                && IsRecoverableInventoryFailure(exception))
            {
                partial = true;
                warningCode = GetInventoryWarningCode(exception);
                warningMessage = GetInventoryWarningMessage(exception);
                _logger.LogWarning(
                    exception,
                    "EFI inventory page {PageNumber} failed; returning {ItemCount} partial items after {RequestCount} requests.",
                    pageNumber,
                    items.Count,
                    requestCount);
                break;
            }
        }

        return new ProviderBankSlipInventoryResult
        {
            Items = items,
            RequestCount = requestCount,
            Truncated = truncated,
            Partial = partial,
            WarningCode = warningCode,
            WarningMessage = warningMessage
        };
    }

    private static string BuildInventoryPath(
        DateTime fromDate,
        DateTime toDate,
        int limit,
        int page)
        => string.Format(
            CultureInfo.InvariantCulture,
            // The list endpoint uses charge categories (billet, card, carnet,
            // subscription). `banking_billet` is the nested payment method and
            // is rejected when used as the charge_type query parameter.
            // EFI reports `offset` but does not use it to advance this endpoint:
            // pagination is page-based. Keeping this explicit also prevents the
            // first page from being requested repeatedly until client timeout.
            "v1/charges?charge_type=billet&begin_date={0:yyyy-MM-dd}&end_date={1:yyyy-MM-dd}&limit={2}&page={3}",
            fromDate,
            toDate,
            limit,
            page);

    private static bool IsRecoverableInventoryFailure(Exception exception)
        => exception is BankSlipGatewayException
            || exception is HttpRequestException
            || exception is TaskCanceledException
            || exception is System.Text.Json.JsonException;

    private static string GetInventoryWarningCode(Exception exception)
        => exception switch
        {
            BankSlipGatewayException gatewayException
                when !string.IsNullOrWhiteSpace(gatewayException.ErrorCode)
                => gatewayException.ErrorCode,
            TaskCanceledException => "provider_timeout",
            System.Text.Json.JsonException => "invalid_provider_response",
            _ => "provider_transport_failure"
        };

    private static string GetInventoryWarningMessage(Exception exception)
        => exception switch
        {
            BankSlipGatewayException gatewayException => gatewayException.Message,
            TaskCanceledException => "A EFI excedeu o tempo de resposta em uma página posterior. Os itens carregados antes do limite foram preservados.",
            System.Text.Json.JsonException => "A EFI devolveu uma página em formato inválido. Os itens carregados anteriormente foram preservados.",
            _ => "A comunicação com a EFI falhou em uma página posterior. Os itens carregados anteriormente foram preservados."
        };

    private static IReadOnlyList<ProviderBankSlipInventoryItem> ParseInventoryPage(JsonElement root)
    {
        var data = GetData(root);
        if (data.ValueKind != JsonValueKind.Array)
            return Array.Empty<ProviderBankSlipInventoryItem>();

        var items = new List<ProviderBankSlipInventoryItem>();
        foreach (var item in data.EnumerateArray())
        {
            var chargeId = GetScalarString(item, "id")
                ?? GetScalarString(item, "charge_id");
            if (string.IsNullOrWhiteSpace(chargeId))
                continue;

            var providerStatus = GetScalarString(item, "status") ?? "unknown";
            var payment = item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("payment", out var paymentElement)
                    ? paymentElement
                    : default;
            items.Add(new ProviderBankSlipInventoryItem
            {
                ChargeId = chargeId,
                CustomId = GetScalarString(item, "custom_id"),
                ProviderStatus = providerStatus,
                Status = MapStatus(providerStatus),
                Value = ReadCents(item, "total") ?? 0m,
                CreatedAtUtc = ReadEfiDateTimeUtc(item, "created_at"),
                PaidAtUtc = ReadEfiDateTimeUtc(payment, "paid_at"),
                PaidValue = ReadCents(payment, "paid_value")
            });
        }

        return items;
    }

    private static decimal? ReadCents(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        decimal cents;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out cents))
            return cents / 100m;
        if (property.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                property.GetString(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out cents))
        {
            return cents / 100m;
        }

        return null;
    }

    private static DateTime? ReadEfiDateTimeUtc(JsonElement element, string propertyName)
    {
        var value = GetScalarString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var hasExplicitOffset = value.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || (value.Length >= 6
                && (value[value.Length - 6] == '+' || value[value.Length - 6] == '-')
                && value[value.Length - 3] == ':');
        if (hasExplicitOffset
            && DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var offset))
        {
            return offset.UtcDateTime;
        }

        if (DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var localProviderTime))
        {
            var unspecified = DateTime.SpecifyKind(localProviderTime, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(unspecified, GetEfiTimeZone());
        }

        return null;
    }

    private static TimeZoneInfo GetEfiTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");
        }
    }
}

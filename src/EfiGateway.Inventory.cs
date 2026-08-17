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
        var requestCount = 0;
        var truncated = false;

        for (var offset = 0; offset < maximumItems; offset += InventoryPageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var limit = Math.Min(InventoryPageSize, maximumItems - offset);
            var path = BuildInventoryPath(fromDate, toDate, limit, offset);
            using var response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, BuildUri(context, path)),
                context,
                BankSlipOperation.Query,
                null,
                cancellationToken).ConfigureAwait(false);
            requestCount++;
            if (response.StatusCode == HttpStatusCode.NotFound)
                break;

            await EnsureSuccessAsync(
                response,
                BankSlipOperation.Query,
                null,
                cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var page = ParseInventoryPage(document.RootElement);
            items.AddRange(page);
            if (page.Count < limit)
                break;

            if (items.Count >= maximumItems)
            {
                truncated = true;
                break;
            }
        }

        return new ProviderBankSlipInventoryResult
        {
            Items = items,
            RequestCount = requestCount,
            Truncated = truncated
        };
    }

    private static string BuildInventoryPath(
        DateTime fromDate,
        DateTime toDate,
        int limit,
        int offset)
        => string.Format(
            CultureInfo.InvariantCulture,
            // The list endpoint uses charge categories (billet, card, carnet,
            // subscription). `banking_billet` is the nested payment method and
            // is rejected when used as the charge_type query parameter.
            "v1/charges?charge_type=billet&begin_date={0:yyyy-MM-dd}&end_date={1:yyyy-MM-dd}&limit={2}&offset={3}",
            fromDate,
            toDate,
            limit,
            offset);

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

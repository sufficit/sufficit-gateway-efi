using Sufficit.Finance;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Sufficit.Gateway.Efi;

/// <summary>
/// Expands opaque Efí callback tokens into provider-neutral status events.
/// </summary>
public sealed partial class EfiGateway : IBankSlipProviderNotificationGateway
{
    public async Task<BankSlipProviderNotificationBatch> GetNotificationAsync(
        string notificationToken,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        ValidateNotificationToken(notificationToken);
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                BuildUri(context, $"v1/notification/{Uri.EscapeDataString(notificationToken.Trim())}")),
            context,
            BankSlipOperation.Reconcile,
            null,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new BankSlipProviderNotificationBatch { ProviderCode = ProviderCode };
        }

        await EnsureSuccessAsync(
            response,
            BankSlipOperation.Reconcile,
            null,
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var data = GetData(document.RootElement);
        var events = new List<BankSlipProviderNotificationEvent>();
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var providerEventId = GetScalarString(item, "id");
                if (string.IsNullOrWhiteSpace(providerEventId))
                {
                    continue;
                }

                var providerStatus = GetNestedScalarString(item, "status", "current");
                events.Add(new BankSlipProviderNotificationEvent
                {
                    EventId = providerEventId,
                    ChargeId = GetNestedScalarString(item, "identifiers", "charge_id"),
                    CustomId = GetScalarString(item, "custom_id"),
                    EventType = GetScalarString(item, "type"),
                    ProviderStatus = providerStatus,
                    Status = string.IsNullOrWhiteSpace(providerStatus)
                        ? null
                        : MapStatus(providerStatus),
                    EventAtUtc = ParseProviderDateTime(GetScalarString(item, "created_at")),
                    PaidAtUtc = ParseProviderDateTime(GetScalarString(item, "received_by_bank_at")),
                    Value = GetProviderValue(item),
                    Payload = item.GetRawText()
                });
            }
        }

        return new BankSlipProviderNotificationBatch
        {
            ProviderCode = ProviderCode,
            Events = events
        };
    }

    private static void ValidateNotificationToken(string notificationToken)
    {
        if (string.IsNullOrWhiteSpace(notificationToken)
            || notificationToken.Trim().Length > 200)
        {
            throw new ArgumentException(
                "Efí notification token is required and must contain at most 200 characters.",
                nameof(notificationToken));
        }
    }

    private static string? GetNestedScalarString(
        JsonElement element,
        string parentPropertyName,
        string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(parentPropertyName, out var parent)
                ? GetScalarString(parent, propertyName)
                : null;

    private static DateTime? ParseProviderDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            return parsed.UtcDateTime;
        }

        return null;
    }

    private static decimal? GetProviderValue(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("value", out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var cents))
        {
            return cents / 100m;
        }

        return null;
    }
}

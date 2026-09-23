using Microsoft.Extensions.Logging;
using Sufficit.Finance;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Sufficit.Gateway.Efi;

/// <summary>
/// Expands opaque Efí callback tokens into provider-neutral status events.
/// </summary>
public sealed partial class EfiGateway : IBankSlipProviderNotificationGateway, IBankSlipPaymentEvidenceReader
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
                    EventAtUtc = ReadEfiDateTimeUtc(item, "created_at"),
                    PaidAtUtc = ReadEfiDateTimeUtc(item, "received_by_bank_at"),
                    // Same source field as PaidAtUtc: the bank receipt day. It
                    // feeds the pending-receipt evidence when the status is not
                    // paid yet, and stays null when Efí omits it.
                    ReceivedByBankAtUtc = ReadEfiDateTimeUtc(item, "received_by_bank_at"),
                    Value = GetProviderValue(item),
                    Payload = item.GetRawText()
                });
            }
        }

        await EnrichPendingReceiptsAsync(events, context, cancellationToken).ConfigureAwait(false);

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

    public BankSlipPaymentEvidence? ReadPaymentEvidence(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var item = document.RootElement;
            var status = GetNestedScalarString(item, "status", "current");
            var chargeId = GetNestedScalarString(item, "identifiers", "charge_id");
            var receivedAt = ReadEfiDateTimeUtc(item, "received_by_bank_at");
            if (string.IsNullOrWhiteSpace(status) || !IsPaidProviderStatus(status)
                || string.IsNullOrWhiteSpace(chargeId) || !receivedAt.HasValue)
                return null;
            return new BankSlipPaymentEvidence
            {
                ChargeId = chargeId,
                ReceivedAtUtc = receivedAt.Value,
                IsDateOnly = DateTime.TryParseExact(
                    GetScalarString(item, "received_by_bank_at"), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            };
        }
        catch (System.Text.Json.JsonException) { return null; }
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

    /// <summary>
    /// Efí notification payloads omit the banking receipt day while a charge is
    /// still "identified". The charge detail carries it, so every pending
    /// charge is enriched from its own authoritative endpoint. A failed detail
    /// lookup keeps the batch alive: the receipt day simply stays unknown
    /// until the provider confirms the payment.
    /// </summary>
    private async Task EnrichPendingReceiptsAsync(
        List<BankSlipProviderNotificationEvent> events,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        var pendingGroups = events
            .Where(item => !string.IsNullOrWhiteSpace(item.ChargeId))
            .Where(item => string.Equals(
                    item.ProviderStatus?.Trim(),
                    "identified",
                    StringComparison.OrdinalIgnoreCase)
                && !item.ReceivedByBankAtUtc.HasValue)
            .GroupBy(item => item.ChargeId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pendingGroups.Length == 0)
            return;

        foreach (var group in pendingGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var detail = await GetAsync(
                    group.Key!,
                    context,
                    cancellationToken).ConfigureAwait(false);
                if (detail?.PaidAtUtc is not { } receivedByBankAtUtc)
                    continue;

                // ProviderBankSlipResult keeps the banking receipt in PaidAtUtc
                // (payment.received_by_bank_at). Each field fills only its own
                // destination; neither stands in for the other.
                foreach (var item in group)
                    item.ReceivedByBankAtUtc = receivedByBankAtUtc;
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested
                && IsRecoverableInventoryFailure(exception))
            {
                _logger.LogWarning(
                    exception,
                    "Could not read the banking receipt day for pending Efí charge {ChargeId}.",
                    group.Key);
            }
        }
    }
}

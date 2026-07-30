using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sufficit.Finance;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sufficit.Gateway.Efi;

/// <summary>
/// Implements the Efí Billing API two-step bank slip flow without the legacy SDK.
/// </summary>
public sealed class EfiBankSlipGateway : IBankSlipGateway, IBankSlipProviderDiagnosticsGateway
{
    public const string HttpClientName = "Sufficit.BankSlips.Efi";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IBankSlipCredentialResolver _credentialResolver;
    private readonly IOptionsMonitor<EfiBankSlipGatewayOptions> _options;
    private readonly ILogger<EfiBankSlipGateway> _logger;
    private readonly ConcurrentDictionary<string, EfiAccessToken> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tokenLocks = new(StringComparer.Ordinal);

    public EfiBankSlipGateway(
        IHttpClientFactory httpClientFactory,
        IBankSlipCredentialResolver credentialResolver,
        IOptionsMonitor<EfiBankSlipGatewayOptions> options,
        ILogger<EfiBankSlipGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _credentialResolver = credentialResolver;
        _options = options;
        _logger = logger;
    }

    public string ProviderCode => BankSlipProviderCodes.Efi;

    public async Task<BankSlipProviderDiagnosticGatewayResult?> ExecuteDiagnosticAsync(
        BankSlipProviderDiagnosticParameters parameters,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.Operation == BankSlipProviderDiagnosticOperation.Authentication)
        {
            _ = await GetAccessTokenAsync(
                context,
                forceRefresh: true,
                cancellationToken).ConfigureAwait(false);
            using var authenticationDocument = JsonDocument.Parse(
                """{"authenticated":true,"credentialValidated":true}""");
            return new BankSlipProviderDiagnosticGatewayResult
            {
                HttpStatusCode = (int)HttpStatusCode.OK,
                Payload = authenticationDocument.RootElement.Clone()
            };
        }

        if (parameters.Operation != BankSlipProviderDiagnosticOperation.Charge)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters),
                parameters.Operation,
                "Unsupported Efí diagnostic operation.");
        }

        ValidateProviderChargeId(parameters.ProviderChargeId!);
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                BuildUri(
                    context,
                    $"v1/charge/{Uri.EscapeDataString(parameters.ProviderChargeId!)}")),
            context,
            BankSlipOperation.Query,
            parameters.ProviderChargeId,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new BankSlipProviderDiagnosticGatewayResult
        {
            HttpStatusCode = (int)response.StatusCode,
            Payload = document.RootElement.Clone()
        };
    }

    public async Task<ProviderBankSlipResult> CreateAsync(
        BankSlipGatewayIssueRequest request,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        ValidateIssueRequest(request);

        var chargeId = string.IsNullOrWhiteSpace(request.ProviderChargeId)
            ? await CreateChargeAsync(request, context, cancellationToken).ConfigureAwait(false)
            : request.ProviderChargeId;
        return await AssignBankSlipAsync(request, chargeId, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderBankSlipResult?> GetAsync(
        string providerChargeId,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        ValidateProviderChargeId(providerChargeId);
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, BuildUri(context, $"v1/charge/{Uri.EscapeDataString(providerChargeId)}")),
            context,
            BankSlipOperation.Query,
            providerChargeId,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, BankSlipOperation.Query, providerChargeId, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseBankSlip(document.RootElement, providerChargeId);
    }

    public async Task<ProviderBankSlipCancellationResult> CancelAsync(
        string providerChargeId,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        ValidateProviderChargeId(providerChargeId);
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Put, BuildUri(context, $"v1/charge/{Uri.EscapeDataString(providerChargeId)}/cancel")),
            context,
            BankSlipOperation.Cancel,
            providerChargeId,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, BankSlipOperation.Cancel, providerChargeId, cancellationToken).ConfigureAwait(false);

        return new ProviderBankSlipCancellationResult
        {
            ProviderCode = ProviderCode,
            ChargeId = providerChargeId,
            ProviderStatus = "canceled",
            Canceled = true
        };
    }

    private async Task<string> CreateChargeAsync(
        BankSlipGatewayIssueRequest request,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        var amountInCents = checked((int)decimal.Round(request.Value * 100m, 0, MidpointRounding.AwayFromZero));
        var metadata = new Dictionary<string, object?>
        {
            ["custom_id"] = request.BankSlipId.ToString("N"),
            ["notification_url"] = request.NotificationUrl?.AbsoluteUri
        };
        var payload = new
        {
            items = new[]
            {
                new
                {
                    name = string.IsNullOrWhiteSpace(request.Description) ? "Serviços Sufficit" : request.Description,
                    value = amountInCents,
                    amount = 1
                }
            },
            metadata
        };

        using var response = await SendAuthorizedAsync(
            () => CreateJsonRequest(HttpMethod.Post, BuildUri(context, "v1/charge"), payload),
            context,
            BankSlipOperation.Issue,
            null,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, BankSlipOperation.Issue, null, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var data = GetData(document.RootElement);
        var chargeId = GetScalarString(data, "charge_id");

        if (string.IsNullOrWhiteSpace(chargeId))
        {
            throw new BankSlipGatewayException(
                BankSlipErrorCategory.AmbiguousResult,
                "efi_missing_charge_id",
                "Efí accepted the charge request but did not return a charge identifier.");
        }

        return chargeId;
    }

    private async Task<ProviderBankSlipResult> AssignBankSlipAsync(
        BankSlipGatewayIssueRequest request,
        string chargeId,
        BankSlipGatewayContext context,
        CancellationToken cancellationToken)
    {
        var payer = CreatePayerPayload(request.Payer);
        var bankingBillet = new Dictionary<string, object?>
        {
            ["customer"] = payer,
            ["expire_at"] = request.Expiration.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        var payload = new Dictionary<string, object?>
        {
            ["payment"] = new Dictionary<string, object?>
            {
                ["banking_billet"] = bankingBillet
            }
        };

        using var response = await SendAuthorizedAsync(
            () => CreateJsonRequest(
                HttpMethod.Post,
                BuildUri(context, $"v1/charge/{Uri.EscapeDataString(chargeId)}/pay"),
                payload),
            context,
            BankSlipOperation.Issue,
            chargeId,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, BankSlipOperation.Issue, chargeId, cancellationToken).ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseBankSlip(document.RootElement, chargeId);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory,
        BankSlipGatewayContext context,
        BankSlipOperation operation,
        string? providerChargeId,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = _options.CurrentValue.Timeout;

        for (var authenticationAttempt = 0; authenticationAttempt < 2; authenticationAttempt++)
        {
            var token = await GetAccessTokenAsync(
                context,
                forceRefresh: authenticationAttempt > 0,
                cancellationToken).ConfigureAwait(false);
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreateTransportException(operation, "efi_timeout", providerChargeId, exception);
            }
            catch (HttpRequestException exception)
            {
                throw CreateTransportException(operation, "efi_transport_error", providerChargeId, exception);
            }

            if (response.StatusCode != HttpStatusCode.Unauthorized || authenticationAttempt > 0)
            {
                return response;
            }

            response.Dispose();
            InvalidateToken(context);
            _logger.LogInformation(
                "Efí access token was rejected for tenant {TenantId}; one controlled reauthentication will be attempted.",
                context.TenantId);
        }

        throw new InvalidOperationException("Unreachable Efí authentication state.");
    }

    private async Task<string> GetAccessTokenAsync(
        BankSlipGatewayContext context,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cacheKey = GetTokenCacheKey(context);
        if (!forceRefresh && TryGetValidToken(cacheKey, out var cachedToken))
        {
            return cachedToken;
        }

        var tokenLock = _tokenLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && TryGetValidToken(cacheKey, out cachedToken))
            {
                return cachedToken;
            }

            var credential = await _credentialResolver
                .GetRequiredAsync(ProviderCode, context, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(credential.ClientId) || string.IsNullOrWhiteSpace(credential.ClientSecret))
            {
                throw new BankSlipGatewayException(
                    BankSlipErrorCategory.DefinitiveRejection,
                    "efi_credentials_missing",
                    "Efí credentials are not configured for the selected tenant.");
            }

            using var request = CreateJsonRequest(
                HttpMethod.Post,
                BuildUri(context, "v1/authorize"),
                new { grant_type = "client_credentials" });
            var basicValue = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{credential.ClientId}:{credential.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicValue);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            client.Timeout = _options.CurrentValue.Timeout;

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, BankSlipOperation.Query, null, cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var accessToken = GetScalarString(document.RootElement, "access_token");
            var expiresIn = GetInt32(document.RootElement, "expires_in") ?? 600;

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new BankSlipGatewayException(
                    BankSlipErrorCategory.ProviderUnavailable,
                    "efi_access_token_missing",
                    "Efí authorization did not return an access token.");
            }

            var skew = _options.CurrentValue.TokenClockSkew;
            var token = new EfiAccessToken
            {
                Value = accessToken,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn).Subtract(skew)
            };
            _tokens[cacheKey] = token;
            return token.Value;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BankSlipGatewayException(
                BankSlipErrorCategory.ProviderUnavailable,
                "efi_authorization_timeout",
                "Efí authorization timed out.",
                innerException: exception);
        }
        catch (HttpRequestException exception)
        {
            throw new BankSlipGatewayException(
                BankSlipErrorCategory.ProviderUnavailable,
                "efi_authorization_transport_error",
                "Efí authorization could not be reached.",
                innerException: exception);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private bool TryGetValidToken(string cacheKey, out string accessToken)
    {
        if (_tokens.TryGetValue(cacheKey, out var token) && token.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            accessToken = token.Value;
            return true;
        }

        accessToken = string.Empty;
        return false;
    }

    private void InvalidateToken(BankSlipGatewayContext context)
        => _tokens.TryRemove(GetTokenCacheKey(context), out _);

    private static string GetTokenCacheKey(BankSlipGatewayContext context)
        => $"{context.TenantId:N}:{(byte)context.Environment}:{context.CredentialReference}";

    private Uri BuildUri(BankSlipGatewayContext context, string relativePath)
    {
        var options = _options.CurrentValue;
        var baseAddress = context.Environment == BankSlipProviderEnvironment.Production
            ? options.ProductionBaseAddress
            : options.SandboxBaseAddress;
        return new Uri(baseAddress, relativePath);
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, Uri uri, object payload)
        => new(method, uri)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };

    private static Dictionary<string, object?> CreatePayerPayload(BankSlipPayerSnapshot payer)
    {
        var document = OnlyDigits(payer.Document);
        var customer = new Dictionary<string, object?>
        {
            ["name"] = payer.Name,
            ["email"] = payer.Email,
            ["phone_number"] = NormalizeBrazilianPhone(OnlyDigits(payer.Phone))
        };

        if (document.Length == 14)
        {
            customer["juridical_person"] = new Dictionary<string, object?>
            {
                ["corporate_name"] = string.IsNullOrWhiteSpace(payer.CorporateName) ? payer.Name : payer.CorporateName,
                ["cnpj"] = document
            };
        }
        else
        {
            customer["cpf"] = document;
        }

        if (payer.Address is not null)
        {
            customer["address"] = new Dictionary<string, object?>
            {
                ["street"] = payer.Address.Street,
                ["number"] = payer.Address.Number,
                ["neighborhood"] = payer.Address.Neighborhood,
                ["zipcode"] = OnlyDigits(payer.Address.PostalCode),
                ["city"] = payer.Address.City,
                ["complement"] = payer.Address.Complement,
                ["state"] = payer.Address.State
            };
        }

        return customer;
    }

    private static ProviderBankSlipResult ParseBankSlip(JsonElement root, string fallbackChargeId)
    {
        var data = GetData(root);
        var chargeId = GetScalarString(data, "charge_id") ?? fallbackChargeId;
        var providerStatus = FindString(data, "status") ?? "waiting";
        var barCode = FindString(data, "barcode", "bar_code", "line");
        var urlValue = FindString(data, "link", "charge", "url");

        return new ProviderBankSlipResult
        {
            ProviderCode = BankSlipProviderCodes.Efi,
            ChargeId = chargeId,
            ProviderStatus = providerStatus,
            Status = MapStatus(providerStatus),
            BarCode = barCode,
            Url = Uri.TryCreate(urlValue, UriKind.Absolute, out var url) ? url : null
        };
    }

    private static BankSlipStatus MapStatus(string providerStatus)
        => providerStatus.ToLowerInvariant() switch
        {
            "new" => BankSlipStatus.Processing,
            "waiting" => BankSlipStatus.Ready,
            "unpaid" => BankSlipStatus.Ready,
            "expired" => BankSlipStatus.Ready,
            "paid" => BankSlipStatus.Paid,
            "settled" => BankSlipStatus.Paid,
            "canceled" => BankSlipStatus.Canceled,
            _ => BankSlipStatus.ReconciliationPending
        };

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        BankSlipOperation operation,
        string? providerChargeId,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
        var statusCode = (int)response.StatusCode;
        var category = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => BankSlipErrorCategory.Validation,
            HttpStatusCode.Unauthorized => BankSlipErrorCategory.DefinitiveRejection,
            HttpStatusCode.Forbidden => BankSlipErrorCategory.SecurityBlock,
            HttpStatusCode.Conflict => BankSlipErrorCategory.SecurityBlock,
            HttpStatusCode.UnprocessableEntity => BankSlipErrorCategory.Validation,
            HttpStatusCode.TooManyRequests
                when operation == BankSlipOperation.Issue
                    && string.IsNullOrWhiteSpace(providerChargeId)
                => BankSlipErrorCategory.AmbiguousResult,
            HttpStatusCode.TooManyRequests => BankSlipErrorCategory.Retryable,
            _ when statusCode >= 500 && operation == BankSlipOperation.Issue => BankSlipErrorCategory.AmbiguousResult,
            _ when statusCode >= 500 => BankSlipErrorCategory.ProviderUnavailable,
            _ when operation == BankSlipOperation.Issue => BankSlipErrorCategory.AmbiguousResult,
            _ => BankSlipErrorCategory.DefinitiveRejection
        };
        var guidance = EfiBankSlipErrorCatalog.Resolve(
            error.Code,
            error.Name,
            category,
            operation);
        category = guidance.Category ?? category;

        throw new BankSlipGatewayException(
            category,
            error.Code ?? $"efi_http_{statusCode}",
            BuildProviderErrorMessage(operation, error.Description),
            statusCode,
            providerChargeId,
            errorName: error.Name,
            errorTitle: guidance.Title,
            errorAction: guidance.Action);
    }

    private static BankSlipGatewayException CreateTransportException(
        BankSlipOperation operation,
        string errorCode,
        string? providerChargeId,
        Exception exception)
    {
        var category = operation == BankSlipOperation.Issue
                ? BankSlipErrorCategory.AmbiguousResult
                : BankSlipErrorCategory.ProviderUnavailable;
        var guidance = EfiBankSlipErrorCatalog.Resolve(
            errorCode,
            null,
            category,
            operation);
        return new BankSlipGatewayException(
            category,
            errorCode,
            $"Efí {operation.ToString().ToLowerInvariant()} transport failed.",
            providerChargeId: providerChargeId,
            innerException: exception,
            errorTitle: guidance.Title,
            errorAction: guidance.Action);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<EfiErrorDetails> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            return new EfiErrorDetails
            {
                Code = GetScalarString(root, "code")
                    ?? GetScalarString(root, "error"),
                Name = GetScalarString(root, "error"),
                Description = ReadErrorDescription(root)
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return new EfiErrorDetails();
        }
    }

    private static string BuildProviderErrorMessage(
        BankSlipOperation operation,
        string? description)
    {
        var operationName = operation switch
        {
            BankSlipOperation.Issue => "a emissão",
            BankSlipOperation.Cancel => "o cancelamento",
            BankSlipOperation.Query => "a consulta",
            _ => "a operação"
        };
        var prefix = $"A Efí rejeitou {operationName}.";
        return string.IsNullOrWhiteSpace(description)
            ? prefix
            : $"{prefix} {TruncateSanitized(description, 700)}";
    }

    private static string? ReadErrorDescription(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error_description", out var description))
        {
            if (description.ValueKind == JsonValueKind.String)
            {
                return description.GetString();
            }

            if (description.ValueKind == JsonValueKind.Object)
            {
                var property = GetScalarString(description, "property");
                var message = GetScalarString(description, "message")
                    ?? FindString(description, "description");
                if (!string.IsNullOrWhiteSpace(property)
                    && !string.IsNullOrWhiteSpace(message))
                {
                    return $"{property}: {message}";
                }

                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }

        return GetScalarString(root, "message");
    }

    private static string TruncateSanitized(string value, int maximumLength)
    {
        var sanitized = new string(value
            .Where(character => !char.IsControl(character) || char.IsWhiteSpace(character))
            .ToArray())
            .Trim();
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized.Substring(0, maximumLength);
    }

    private static JsonElement GetData(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;

    private static string? GetScalarString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static int? GetInt32(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.TryGetInt32(out var result)
                ? result
                : null;

    private static string? FindString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in propertyNames)
            {
                var value = GetScalarString(element, propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindString(property.Value, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, propertyNames);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string OnlyDigits(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    private static string NormalizeBrazilianPhone(string phone)
        => phone.Length is 12 or 13 && phone.StartsWith("55", StringComparison.Ordinal)
            ? phone.Substring(2)
            : phone;

    private static void ValidateIssueRequest(BankSlipGatewayIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payer);

        if (request.BankSlipId == Guid.Empty || request.ContextId == Guid.Empty)
        {
            throw new ArgumentException("Bank slip and context identifiers are required.", nameof(request));
        }

        if (request.Value <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Bank slip value must be positive.");
        }

        var documentLength = OnlyDigits(request.Payer.Document).Length;
        if (documentLength != 11 && documentLength != 14)
        {
            throw new ArgumentException("Payer document must be a CPF or CNPJ.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Payer.Name))
        {
            throw new ArgumentException("Payer name is required.", nameof(request));
        }

        var phone = NormalizeBrazilianPhone(OnlyDigits(request.Payer.Phone));
        if (phone.Length is not 10 and not 11)
        {
            throw CreateValidationException(
                "efi_payer_phone_invalid",
                "Efí bank slips require a Brazilian phone with 10 or 11 digits.");
        }

        var address = request.Payer.Address;
        if (address is null)
        {
            throw CreateValidationException(
                "efi_payer_address_missing",
                "Efí bank slips require the payer address.");
        }

        if (string.IsNullOrWhiteSpace(address.Street))
        {
            throw CreateValidationException(
                "efi_payer_address_street_missing",
                "Efí bank slips require the payer street.");
        }

        if (string.IsNullOrWhiteSpace(address.Number))
        {
            throw CreateValidationException(
                "efi_payer_address_number_missing",
                "Efí bank slips require the payer address number.");
        }

        if (string.IsNullOrWhiteSpace(address.Neighborhood)
            || string.IsNullOrWhiteSpace(address.City)
            || OnlyDigits(address.PostalCode).Length != 8
            || address.State?.Trim().Length != 2)
        {
            throw CreateValidationException(
                "efi_payer_address_incomplete",
                "Efí bank slips require neighborhood, city, an 8-digit postal code and a 2-letter state.");
        }
    }

    private static BankSlipGatewayException CreateValidationException(
        string errorCode,
        string message)
    {
        var guidance = EfiBankSlipErrorCatalog.Resolve(
            errorCode,
            null,
            BankSlipErrorCategory.Validation,
            BankSlipOperation.Issue);
        return new BankSlipGatewayException(
            BankSlipErrorCategory.Validation,
            errorCode,
            message,
            errorTitle: guidance.Title,
            errorAction: guidance.Action);
    }

    private static void ValidateProviderChargeId(string providerChargeId)
    {
        if (string.IsNullOrWhiteSpace(providerChargeId))
        {
            throw new ArgumentException("Provider charge identifier is required.", nameof(providerChargeId));
        }
    }

    private sealed class EfiErrorDetails
    {
        public string? Code { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
    }
}

namespace Sufficit.Gateway.Efi;

internal sealed class EfiAccessToken
{
    public string Value { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; init; }
}

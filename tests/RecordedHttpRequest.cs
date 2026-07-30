namespace Sufficit.Gateway.Efi.Tests;

internal sealed class RecordedHttpRequest
{
    public HttpMethod Method { get; init; } = HttpMethod.Get;
    public Uri Uri { get; init; } = default!;
    public IReadOnlyDictionary<string, string[]> Headers { get; init; } = default!;
    public string? Body { get; init; }
}

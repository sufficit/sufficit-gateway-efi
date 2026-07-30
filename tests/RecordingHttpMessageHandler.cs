using System.Net;
using System.Text;

namespace Sufficit.Gateway.Efi.Tests;

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public IList<RecordedHttpRequest> Requests { get; } = new List<RecordedHttpRequest>();

    public void EnqueueJson(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => _responses.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(value => value.Key, value => value.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedHttpRequest
        {
            Method = request.Method,
            Uri = request.RequestUri!,
            Headers = headers,
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken)
        });

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No fake HTTP response was configured.");
        }

        return _responses.Dequeue()(request);
    }
}

using System.Net;
using System.Text;

namespace Sev.Client.Tests;

// Stands in for the network: returns canned responses and records every request,
// so SevClient can be tested without calling the real API.
public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public void Respond(string body, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "application/json")
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        });
    }

    public void RespondWithFixture(string fileName) =>
        Respond(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        return _responses.Count > 0
            ? _responses.Dequeue()
            : throw new InvalidOperationException($"No response queued for {request.RequestUri}");
    }
}

public record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

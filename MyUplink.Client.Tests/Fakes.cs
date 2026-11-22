using System.Net;
using System.Text;

namespace MyUplink.Client.Tests;

// Stands in for the network: returns queued responses and records every request, so no test calls myUplink.
public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public HttpResponseMessage Respond(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        _responses.Enqueue(response);
        return response;
    }

    public HttpResponseMessage RespondWithFixture(string fileName) =>
        Respond(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName)));

    public void RespondWithToken(string token = "test-access-token", int expiresIn = 3600) =>
        Respond($$"""{"access_token":"{{token}}","expires_in":{{expiresIn}},"token_type":"Bearer","scope":"READSYSTEM"}""");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.UserAgent.ToString(),
            request.Headers.AcceptLanguage.ToString(),
            body));

        return _responses.Count > 0
            ? _responses.Dequeue()
            : throw new InvalidOperationException($"No response queued for {request.RequestUri}");
    }
}

public record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string UserAgent, string AcceptLanguage, string? Body);

// A clock that only moves when the test says so.
public class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

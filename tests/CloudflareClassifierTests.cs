using System.Net;
using System.Text.Json;
using Ecs.Client;
using Ticket.Adapter.Cloudflare;
using Ticket.Data;
using Xunit;

namespace Ticket.Adapter.Cloudflare.Tests;

public class CloudflareClassifierTests
{
    private sealed class RecordingWorld : IWorldClient
    {
        public List<object> Requests { get; } = [];
        private readonly Dictionary<Type, Func<object, Task>> _handlers = [];

        public Task<TResponse> AskAsync<TRequest, TResponse>(
            TRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request!);
            return Task.FromResult<TResponse>(default!);
        }

        public IDisposable Subscribe<TNotification>(Func<TNotification, Task> handler)
        {
            _handlers[typeof(TNotification)] = n => handler((TNotification)n);
            return new Noop();
        }

        public async Task Raise<TNotification>(TNotification notification)
        {
            if (_handlers.TryGetValue(typeof(TNotification), out var handler))
                await handler(notification!);
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class StubHttp(params HttpResponseMessage[] replies) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Sent { get; } = [];
        private int _call;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((request, request.Content is null
                ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return replies[Math.Min(_call++, replies.Length - 1)];
        }
    }

    private static HttpResponseMessage Json(string body, bool ok = true) => new(
        ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private const string ClefReply =
        """{"result":{"answers":{"priority":{"type":"choice","choice":"no-rush","probabilities":{"urgent":0.05,"no-rush":0.9,"report":0.05},"confidence":0.88}}},"success":true}""";

    private static async Task<(PriorityClassified Classified, List<(HttpRequestMessage Request, string? Body)> Sent)> RunAsync(
        ClefOptions options, params HttpResponseMessage[] replies)
    {
        var world = new RecordingWorld();
        var handler = new StubHttp(replies);
        world.AddCloudflareClassifier(new HttpClient(handler), options);
        await world.Raise(new PriorityClassifyRequested { TicketId = "t1", Text = "printer on fire" });
        return (Assert.IsType<PriorityClassified>(world.Requests.Single()), handler.Sent);
    }

    [Fact]
    public async Task CloudflareAnswers_AsksWithTheClefContract_AndMapsTheChoice()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", "tok"), Json(ClefReply));

        Assert.Equal(TicketPriority.NoRush, classified.Priority);
        Assert.False(classified.Offline);
        Assert.Equal("t1", classified.TicketId);

        var (request, raw) = Assert.Single(sent);
        Assert.Equal(
            "https://api.cloudflare.com/client/v4/accounts/acct/ai/run/@cf/cloudflare/clef",
            request.RequestUri!.ToString());
        Assert.Equal("Bearer tok", request.Headers.Authorization!.ToString());

        var body = JsonDocument.Parse(raw!).RootElement;
        Assert.Equal("clef", body.GetProperty("model").GetString());
        Assert.Contains("printer on fire", body.GetProperty("state").GetString());
        var question = body.GetProperty("questions").GetProperty("priority");
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.True(question.GetProperty("criteria").TryGetProperty("urgent", out _));
        Assert.True(question.GetProperty("criteria").TryGetProperty("no-rush", out _));
        Assert.True(question.GetProperty("criteria").TryGetProperty("report", out _));
    }

    [Fact]
    public async Task UnknownChoiceValues_DefaultToUrgent()
    {
        var (classified, _) = await RunAsync(new ClefOptions("acct", "tok"),
            Json("""{"result":{"answers":{"priority":{"choice":"whatever"}}},"success":true}"""));

        Assert.Equal(TicketPriority.Urgent, classified.Priority);
    }

    [Fact]
    public async Task CloudflareUnreachable_AfterRetries_ReportsOffline()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", "tok"),
            Json("""{"success":false,"errors":[{"code":10000}]}""", ok: false),
            Json("""{"success":false,"errors":[{"code":10000}]}""", ok: false));

        Assert.Null(classified.Priority);
        Assert.True(classified.Offline);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task MissingCredentials_ReportsOffline_WithoutCallingCloudflare()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", ""));

        Assert.Null(classified.Priority);
        Assert.True(classified.Offline);
        Assert.Empty(sent);
    }
}

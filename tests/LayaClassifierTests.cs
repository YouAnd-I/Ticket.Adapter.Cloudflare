using System.Net;
using Ecs.Client;
using Ticket.Adapter.Laya;
using Ticket.Data;
using Xunit;

namespace Ticket.Adapter.Laya.Tests;

public class LayaClassifierTests
{
    // Stands in for the whole world: records what the adapter asks,
    // and lets the test deliver a notification the way the loop would.
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
        public List<HttpRequestMessage> Sent { get; } = [];
        private int _call;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(request);
            return Task.FromResult(replies[Math.Min(_call++, replies.Length - 1)]);
        }
    }

    private static HttpResponseMessage Json(string body, bool ok = true) => new(
        ok ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static async Task<PriorityClassified> RunAsync(
        RecordingWorld world, params HttpResponseMessage[] replies)
    {
        var http = new HttpClient(new StubHttp(replies));
        world.AddLayaClassifier(http);
        await world.Raise(new PriorityClassifyRequested { TicketId = "t1", Text = "printer on fire" });
        return Assert.IsType<PriorityClassified>(world.Requests.Single());
    }

    [Fact]
    public async Task LayaAnswers_PostsTheText_AndReportsThePriority()
    {
        var world = new RecordingWorld();
        var classified = await RunAsync(world, Json("""{"priority":"no-rush"}"""));

        Assert.Equal(TicketPriority.NoRush, classified.Priority);
        Assert.False(classified.Offline);
        Assert.Equal("t1", classified.TicketId);
    }

    [Fact]
    public async Task UnknownPriorityValues_DefaultToUrgent()
    {
        var world = new RecordingWorld();
        var classified = await RunAsync(world, Json("""{"priority":"whatever"}"""));

        Assert.Equal(TicketPriority.Urgent, classified.Priority);
    }

    [Fact]
    public async Task LayaDown_AfterRetries_ReportsOffline()
    {
        var world = new RecordingWorld();
        var classified = await RunAsync(world,
            Json("""{"error":"down"}""", ok: false),
            Json("""{"error":"down"}""", ok: false));

        Assert.Null(classified.Priority);
        Assert.True(classified.Offline);
    }
}

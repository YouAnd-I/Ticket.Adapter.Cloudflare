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

    private static PriorityClassifyRequested Notification(
        PriorityOption[]? priorities = null, StaffMember[]? staff = null) => new()
    {
        TicketId = "t1",
        Text = "printer on fire",
        Priorities = priorities ?? [.. PriorityGuidance.Defaults],
        AvailableStaff = staff ?? [],
        NowUtc = "2026-10-07 09:00:00Z",
    };

    private static async Task<(PriorityClassified Classified, List<(HttpRequestMessage Request, string? Body)> Sent)> RunAsync(
        ClefOptions options, PriorityClassifyRequested notification,
        params HttpResponseMessage[] replies)
    {
        var world = new RecordingWorld();
        var handler = new StubHttp(replies);
        world.AddCloudflareClassifier(new HttpClient(handler), options);
        await world.Raise(notification);
        return (Assert.IsType<PriorityClassified>(world.Requests.Single()), handler.Sent);
    }

    [Fact]
    public async Task CloudflareAnswers_AsksWithTheClefContract_AndMapsTheChoice()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", "tok"),
            Notification(), Json(ClefReply));

        Assert.Equal(TicketPriority.NoRush, classified.Priority);
        Assert.False(classified.Offline);
        Assert.Equal("t1", classified.TicketId);
        Assert.Null(classified.AssigneeStaffId);

        var (request, raw) = Assert.Single(sent);
        Assert.Equal(
            "https://api.cloudflare.com/client/v4/accounts/acct/ai/run/@cf/cloudflare/clef",
            request.RequestUri!.ToString());
        Assert.Equal("Bearer tok", request.Headers.Authorization!.ToString());

        var body = JsonDocument.Parse(raw!).RootElement;
        Assert.Equal("clef", body.GetProperty("model").GetString());
        var state = body.GetProperty("state").GetString()!;
        Assert.Contains("2026-10-07 09:00:00Z", state);
        Assert.Contains("printer on fire", state);
        var questions = body.GetProperty("questions");
        var priority = questions.GetProperty("priority");
        Assert.Equal("choice", priority.GetProperty("type").GetString());
        Assert.Equal("Something is broken, failing, or blocking the user right now",
            priority.GetProperty("criteria").GetProperty("urgent").GetString());
        Assert.True(priority.GetProperty("criteria").TryGetProperty("no-rush", out _));
        Assert.False(priority.GetProperty("criteria").TryGetProperty("report", out _));
        Assert.False(questions.TryGetProperty("assignee", out _));
    }

    [Fact]
    public async Task SheetPriorities_ReplaceTheDefaultGuidance()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", "tok"),
            Notification(priorities:
            [
                new PriorityOption("urgent", "mention me immediately, always"),
                new PriorityOption("critical", ""),
            ]),
            Json("""{"result":{"answers":{"priority":{"choice":"urgent"}}},"success":true}"""));

        Assert.Equal(TicketPriority.Urgent, classified.Priority);

        var criteria = JsonDocument.Parse(sent.Single().Body!).RootElement
            .GetProperty("questions").GetProperty("priority").GetProperty("criteria");
        Assert.Equal("mention me immediately, always", criteria.GetProperty("urgent").GetString());
        Assert.Equal("critical", criteria.GetProperty("critical").GetString());
        Assert.False(criteria.TryGetProperty("no-rush", out _));
    }

    [Fact]
    public async Task AssigneeQuestion_OffersOnDutyStaff_AndMapsTheChoiceToAStaffId()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", "tok"),
            Notification(staff:
            [
                new StaffMember("407442087664156674", "Fahim", "wifi, VPN, anything networking"),
                new StaffMember("100", "Bob", ""),
            ]),
            Json("""{"result":{"answers":{"priority":{"choice":"urgent"},"assignee":{"choice":"407442087664156674"}}},"success":true}"""));

        Assert.Equal("407442087664156674", classified.AssigneeStaffId);

        var assignee = JsonDocument.Parse(sent.Single().Body!).RootElement
            .GetProperty("questions").GetProperty("assignee");
        Assert.Equal("Which on-duty IT staff member should this ticket go to?",
            assignee.GetProperty("instructions").GetString());
        Assert.Equal("Fahim — wifi, VPN, anything networking",
            assignee.GetProperty("criteria").GetProperty("407442087664156674").GetString());
        Assert.Equal("Bob", assignee.GetProperty("criteria").GetProperty("100").GetString());
    }

    [Fact]
    public async Task SingleStaffHasNoRealChoice_SkipsTheAssigneeQuestion()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", "tok"),
            Notification(staff: [new StaffMember("100", "Bob", "printers")]),
            Json("""{"result":{"answers":{"priority":{"choice":"urgent"}}},"success":true}"""));

        Assert.Equal(TicketPriority.Urgent, classified.Priority);
        Assert.Null(classified.AssigneeStaffId);
        var questions = JsonDocument.Parse(sent.Single().Body!).RootElement
            .GetProperty("questions").EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(["priority"], questions);
    }

    [Fact]
    public async Task AssigneeChoice_OutsideTheRoster_IsIgnored()
    {
        var (classified, _) = await RunAsync(new ClefOptions("acct", "tok"),
            Notification(staff:
            [
                new StaffMember("100", "Bob", "printers"),
                new StaffMember("200", "Carol", "wifi"),
            ]),
            Json("""{"result":{"answers":{"priority":{"choice":"urgent"},"assignee":{"choice":"999"}}},"success":true}"""));

        Assert.Null(classified.AssigneeStaffId);
    }

    [Fact]
    public async Task UnknownChoiceValues_DefaultToUrgent()
    {
        var (classified, _) = await RunAsync(new ClefOptions("acct", "tok"),
            Notification(),
            Json("""{"result":{"answers":{"priority":{"choice":"whatever"}}},"success":true}"""));

        Assert.Equal(TicketPriority.Urgent, classified.Priority);
    }

    [Fact]
    public async Task CloudflareUnreachable_AfterRetries_ReportsOffline()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", "tok"),
            Notification(),
            Json("""{"success":false,"errors":[{"code":10000}]}""", ok: false),
            Json("""{"success":false,"errors":[{"code":10000}]}""", ok: false));

        Assert.Null(classified.Priority);
        Assert.True(classified.Offline);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task MissingCredentials_ReportsOffline_WithoutCallingCloudflare()
    {
        var (classified, sent) = await RunAsync(new ClefOptions("acct", ""),
            Notification());

        Assert.Null(classified.Priority);
        Assert.True(classified.Offline);
        Assert.Empty(sent);
    }
}

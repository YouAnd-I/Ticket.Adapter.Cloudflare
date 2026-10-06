using System.Text;
using System.Text.Json;
using Ecs.Client;
using Ticket.Data;

namespace Ticket.Adapter.Laya;

// The world's priority classifier, as an adapter: the TicketSystem asks for a
// classification (a PriorityClassifyRequested notification); this POSTs to the
// laya service and answers the world with a PriorityClassified request. When
// laya is down the world is told so (Offline) and defaults to urgent — the bot
// keeps working either way.
public static class LayaClassifier
{
    public static void AddLayaClassifier(this IWorldClient world) =>
        AddLayaClassifier(world, new HttpClient { Timeout = TimeSpan.FromSeconds(5) });

    public static void AddLayaClassifier(this IWorldClient world, HttpClient http) =>
        world.Subscribe<PriorityClassifyRequested>(notification => HandleAsync(world, http, notification));

    public static async Task HandleAsync(
        IWorldClient world, HttpClient http, PriorityClassifyRequested notification)
    {
        var priority = await ClassifyAsync(http, notification.Text).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.AskAsync<PriorityClassified, PriorityClassifiedAck>(new PriorityClassified
        {
            TicketId = notification.TicketId,
            Priority = priority,
            Offline = priority is null,
        }, timeout.Token).ConfigureAwait(false);
    }

    // laya classifier — http://127.0.0.1:8399/classify, one retry, null = offline
    public static async Task<TicketPriority?> ClassifyAsync(HttpClient http, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TicketPriority.NoRush;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var res = await http.PostAsync("http://127.0.0.1:8399/classify",
                    new StringContent(JsonSerializer.Serialize(new { text }),
                        Encoding.UTF8, "application/json")).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                    return JsonDocument.Parse(body).RootElement.GetProperty("priority").GetString() switch
                    {
                        "no-rush" => TicketPriority.NoRush,
                        "report" => TicketPriority.Report,
                        _ => TicketPriority.Urgent,
                    };
                Console.WriteLine($"[laya] HTTP {(int)res.StatusCode}: {body}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[laya] classify failed: {ex.GetType().Name}: {ex.Message}");
            }
            if (attempt == 0) await Task.Delay(1500).ConfigureAwait(false);
        }
        return null;
    }
}

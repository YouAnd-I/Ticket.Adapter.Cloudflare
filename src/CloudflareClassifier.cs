using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ecs.Client;
using Ticket.Data;

namespace Ticket.Adapter.Cloudflare;

// Everything one classifier call needs. FromEnvironment reads what the
// composition root (or the Aspire AppHost) sets: Cloudflare__AccountId /
// Cloudflare__ApiToken.
public sealed record ClefOptions(string AccountId, string ApiToken, string Model = "clef")
{
    public static ClefOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("Cloudflare__AccountId") ?? "",
        Environment.GetEnvironmentVariable("Cloudflare__ApiToken") ?? "");
}

// The world's priority classifier, as an adapter: the TicketSystem asks for a
// classification (a PriorityClassifyRequested notification); this asks the
// Cloudflare Workers AI clef model and answers the world with a
// PriorityClassified request. When Cloudflare is unreachable — or no token is
// configured — the world is told so (Offline) and defaults to urgent; the bot
// keeps working either way.
public static class CloudflareClassifier
{
    public static void AddCloudflareClassifier(this IWorldClient world) =>
        AddCloudflareClassifier(world,
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) }, ClefOptions.FromEnvironment());

    public static void AddCloudflareClassifier(this IWorldClient world, HttpClient http, ClefOptions options) =>
        world.Subscribe<PriorityClassifyRequested>(notification => HandleAsync(world, http, options, notification));

    public static async Task HandleAsync(
        IWorldClient world, HttpClient http, ClefOptions options, PriorityClassifyRequested notification)
    {
        var priority = await ClassifyAsync(http, options, notification.Text).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.AskAsync<PriorityClassified, PriorityClassifiedAck>(new PriorityClassified
        {
            TicketId = notification.TicketId,
            Priority = priority,
            Offline = priority is null,
        }, timeout.Token).ConfigureAwait(false);
    }

    // clef — https://developers.cloudflare.com/workers-ai/models/clef/ — one
    // choice question. The body is flat (no `input` wrapper, unlike most Workers
    // AI models) and `model` is a required field with the bare name. One retry,
    // null = offline.
    public static async Task<TicketPriority?> ClassifyAsync(HttpClient http, ClefOptions options, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TicketPriority.NoRush;
        if (string.IsNullOrWhiteSpace(options.AccountId) || string.IsNullOrWhiteSpace(options.ApiToken))
        {
            Console.WriteLine("[cloudflare] no Cloudflare__AccountId/Cloudflare__ApiToken — priority stays urgent (offline)");
            return null;
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var body = JsonSerializer.Serialize(new
                {
                    model = options.Model,
                    state = $"A user submitted a support ticket: {text}",
                    questions = new
                    {
                        priority = new
                        {
                            type = "choice",
                            instructions = "How should this support ticket be prioritized?",
                            criteria = new Dictionary<string, string>
                            {
                                ["urgent"] = "Something is broken, failing, or blocking the user right now",
                                ["no-rush"] = "A question or a request that can wait; nothing is failing",
                                ["report"] = "The user reports something that needs investigating or documenting, not an immediate fix",
                            },
                        },
                    },
                });
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"https://api.cloudflare.com/client/v4/accounts/{options.AccountId}/ai/run/@cf/cloudflare/{options.Model}")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
                var res = await http.SendAsync(request).ConfigureAwait(false);
                var raw = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                    // {"result":{"answers":{"priority":{"choice":"urgent",...}}},"success":true}
                    return JsonDocument.Parse(raw).RootElement
                        .GetProperty("result").GetProperty("answers").GetProperty("priority")
                        .GetProperty("choice").GetString() switch
                    {
                        "no-rush" => TicketPriority.NoRush,
                        "report" => TicketPriority.Report,
                        _ => TicketPriority.Urgent,
                    };
                Console.WriteLine($"[cloudflare] HTTP {(int)res.StatusCode}: {raw}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[cloudflare] classify failed: {ex.GetType().Name}: {ex.Message}");
            }
            if (attempt == 0) await Task.Delay(1500).ConfigureAwait(false);
        }
        return null;
    }
}

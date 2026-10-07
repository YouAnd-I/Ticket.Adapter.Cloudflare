using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ecs.Client;
using Ticket.Data;

namespace Ticket.Adapter.Cloudflare;

public sealed record ClefOptions(string AccountId, string ApiToken, string Model = "clef")
{
    public static ClefOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("Cloudflare__AccountId") ?? "",
        Environment.GetEnvironmentVariable("Cloudflare__ApiToken") ?? "");
}

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
        var classified = await ClassifyAsync(http, options, notification).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await world.AskAsync<PriorityClassified, PriorityClassifiedAck>(new PriorityClassified
        {
            TicketId = notification.TicketId,
            Priority = classified?.Priority,
            Category = classified?.Category,
            Offline = classified is null,
        }, timeout.Token).ConfigureAwait(false);
    }

    public static async Task<PriorityClassified?> ClassifyAsync(
        HttpClient http, ClefOptions options, PriorityClassifyRequested notification)
    {
        var text = notification.Text;
        if (string.IsNullOrWhiteSpace(text)) return new PriorityClassified { Priority = TicketPriority.NoRush };
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
                    state = $"Today is {notification.NowUtc}. A user submitted a support ticket: {text}",
                    questions = Questions(notification),
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
                {
                    var answers = JsonDocument.Parse(raw).RootElement
                        .GetProperty("result").GetProperty("answers");
                    var priority = answers.GetProperty("priority").GetProperty("choice").GetString() switch
                    {
                        "no-rush" => TicketPriority.NoRush,
                        "report" => TicketPriority.Report,
                        _ => TicketPriority.Urgent,
                    };
                    var category = notification.Categories is { Length: > 0 } &&
                        answers.TryGetProperty("category", out var categoryAnswer)
                        ? categoryAnswer.GetProperty("choice").GetString()
                        : null;
                    return new PriorityClassified { Priority = priority, Category = category };
                }
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

    private static object Questions(PriorityClassifyRequested notification)
    {
        var priority = new
        {
            type = "choice",
            instructions = "How should this support ticket be prioritized?",
            criteria = new Dictionary<string, string>
            {
                ["urgent"] = "Something is broken, failing, or blocking the user right now",
                ["no-rush"] = "A question or a request that can wait; nothing is failing",
                ["report"] = "The user reports something that needs investigating or documenting, not an immediate fix",
            },
        };
        if (notification.Categories is not { Length: > 0 })
            return new { priority };

        var category = new
        {
            type = "choice",
            instructions = "Which category fits this ticket best?",
            criteria = notification.Categories
                .Select(category_ => KeyValuePair.Create(
                    category_.Slug,
                    string.IsNullOrWhiteSpace(category_.Description)
                        ? category_.Slug
                        : category_.Description))
                .Append(KeyValuePair.Create("uncategorized", "None of the categories fit"))
                .ToDictionary(),
        };
        return new { priority, category };
    }
}

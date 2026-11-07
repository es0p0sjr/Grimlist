using System.Net;
using System.Text.RegularExpressions;
using GrimList.Core;

namespace GrimList.Application;

public record PrimaryMissionCard(string Name, string Text, DateTimeOffset RetrievedAt);

// Read published cards on demand; never substitute a guessed scoring rule.
public sealed class PrimaryMissionCards(HttpClient client)
{
    public const string SourceUrl = "https://wahapedia.ru/wh40k11ed/the-rules/warhammer-event-companion/#Primary-Mission-deck";
    public const string SourceLabel = "Chapter Approved 2026-27 · Wahapedia";
    public static PrimaryMissionCards Shared { get; } = new(new HttpClient { Timeout = TimeSpan.FromSeconds(25) });
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, PrimaryMissionCard>? cache;
    private DateTimeOffset retrieved;

    public async Task<PrimaryMissionCard> GetAsync(string name, CancellationToken cancellation = default)
    {
        if (!DispositionPrimaries.IsKnownPrimary(name)) throw new InvalidDataException("Unknown primary mission.");
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (cache == null || DateTimeOffset.UtcNow - retrieved > TimeSpan.FromHours(1))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, SourceUrl.Split('#')[0]);
                request.Headers.UserAgent.ParseAdd("GrimList/0.2");
                using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                var now = DateTimeOffset.UtcNow;
                var parsed = await Task.Run(() => Parse(html, now), cancellation).ConfigureAwait(false);
                cache = parsed; retrieved = now;
            }
            return cache.TryGetValue(UnitIdentity.Normalize(name), out var card) ? card
                : throw new InvalidDataException("The published card could not be found. Open the source to check it.");
        }
        finally { gate.Release(); }
    }

    public static Dictionary<string, PrimaryMissionCard> Parse(string html, DateTimeOffset retrievedAt)
    {
        var start = html.IndexOf("<a name=\"Primary-Mission-deck\"", StringComparison.Ordinal);
        var end = html.IndexOf("<a name=\"Deployment-deck\"", Math.Max(0, start), StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new InvalidDataException("The mission source format has changed. Open the source to view the card.");
        var section = html[start..end];
        var cards = new Dictionary<string, PrimaryMissionCard>();
        foreach (Match match in Regex.Matches(section, "<div class=\"cgCardCA7 BreakInsideAvoid\">"))
        {
            var cardHtml = Div(section, match.Index);
            var title = Regex.Match(cardHtml, "<div class=\"ca7Name\">(?<name>.*?)</div>", RegexOptions.Singleline);
            var name = PlainText(title.Groups["name"].Value);
            if (!DispositionPrimaries.IsKnownPrimary(name)) continue;
            var bodyStart = cardHtml.IndexOf("<div class=\"ca7Body\">", StringComparison.Ordinal);
            if (bodyStart < 0) throw new InvalidDataException("The published card has no readable scoring section.");
            var body = Div(cardHtml, bodyStart);
            var scores = Regex.Matches(body, "<div class=\"caPmScore\">").Cast<Match>().ToList();
            // Separate complete scoring conditions, keeping their VP and qualifiers together.
            foreach (var score in scores.Take(Math.Max(0, scores.Count - 1)).Reverse())
                body = body.Insert(score.Index + Div(body, score.Index).Length,
                    "<div>===========================================</div>");
            var text = PlainText(body);
            if (!text.Contains("VP", StringComparison.Ordinal) || text.Length < 30)
                throw new InvalidDataException("The published scoring section is incomplete.");
            if (!cards.TryAdd(UnitIdentity.Normalize(name), new(name, text, retrievedAt)))
                throw new InvalidDataException("The mission source contains duplicate cards.");
        }
        if (cards.Count != 25) throw new InvalidDataException("The primary mission deck is incomplete. Open the source to view the card.");
        return cards;
    }

    private static string Div(string html, int start)
    {
        var depth = 0;
        foreach (Match tag in Regex.Matches(html[start..], @"</?div\b[^>]*>", RegexOptions.IgnoreCase))
        {
            depth += tag.Value.StartsWith("</", StringComparison.Ordinal) ? -1 : 1;
            if (depth == 0) return html.Substring(start, tag.Index + tag.Length);
        }
        throw new InvalidDataException("The published mission card is truncated.");
    }

    private static string PlainText(string html)
    {
        html = Regex.Replace(html, @"<(script|style)\b[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        // Underlined 'one' means exactly one in this mission pack; plain text must retain that distinction.
        html = Regex.Replace(html, @"<u>\s*one\s*</u>", "exactly one", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<span class=\"caPmOr\">OR</span>", "OR: ");
        html = Regex.Replace(html, "<span class=\"caPmPlus\">\\+</span>", "+ ");
        html = Regex.Replace(html, "<span class=\"caPmVP\">", " — ");
        html = Regex.Replace(html, "<span class=\"caPmCumul\">", " ");
        html = Regex.Replace(html, @"<br\s*/?>|</(?:div|p|li|tr)>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", "");
        html = WebUtility.HtmlDecode(html).Replace('\u00a0', ' ');
        return string.Join("\n", html.Split('\n').Select(s => Regex.Replace(s, @"\s+", " ").Trim()).Where(s => s.Length > 0));
    }
}

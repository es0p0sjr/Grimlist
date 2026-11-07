using System.Net;
using GrimList.Application;
using GrimList.Core;

internal static class PrimaryMissionCardChecks
{
    public static void Run(Action<string, Action> test, string[] args)
    {
        var source = new SourceInfo("Fixture", "1", "", "", Edition: "11");
        string[] dispositions = ["Take and Hold", "Purge the Foe", "Disruption", "Reconnaissance", "Priority Assets"];
        var data = new Dataset(source, [], Detachments: [new("Fixture", "Orders", Dispositions: [.. dispositions])]);
        var army = ArmyList.New("Fixture") with { Detachments = ["Orders"] };
        var names = dispositions.SelectMany(d => DispositionPrimaries.ForArmy(army with { Disposition = d }, data).Matchups)
            .Select(m => m.YourPrimary).Distinct().ToList();
        // Synthetic scoring text tests markup semantics without duplicating the published deck.
        var html = "<a name=\"Primary-Mission-deck\"></a>" + string.Concat(names.Select(n =>
            "<div class=\"cgCardCA7 BreakInsideAvoid\"><div class=\"ca7Name\">" + WebUtility.HtmlEncode(n) + "</div>" +
            "<p>Flavour text excluded</p><div class=\"ca7Body\"><div>ROUND THREE</div><div>WHEN: End of turn.</div>" +
            "<div><span>Hold <u>one</u> marker &amp; finish the action.</span><span class=\"caPmVP\">2VP</span></div>" +
            "<div><span class=\"caPmOr\">OR</span><span>Alternative.</span><span class=\"caPmVP\">3VP</span></div>" +
            "<div><span class=\"caPmPlus\">+</span><span>Bonus.</span><span class=\"caPmVP\">+1VP</span><span class=\"caPmCumul\">CUMULATIVE</span></div></div>" +
            "<div>Opponent icons excluded</div></div>")) + "<a name=\"Deployment-deck\"></a>";
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
        test("Primary card reader preserves scoring, timing, exact-one, OR and cumulative conditions for all 25 cards", () =>
        {
            var cards = PrimaryMissionCards.Parse(html, DateTimeOffset.UtcNow);
            Check(cards.Count == 25, "Missing card");
            foreach (var name in names)
            {
                var text = cards[UnitIdentity.Normalize(name)].Text;
                Check(text.Contains("ROUND THREE\nWHEN: End of turn.") && text.Contains("exactly one marker & finish the action. — 2VP")
                    && text.Contains("OR: Alternative. — 3VP") && text.Contains("+1VP CUMULATIVE"), "Scoring formatting lost");
                Check(!text.Contains("excluded") && !text.Contains('<'), "Unrelated content or markup leaked");
            }
            foreach (var broken in new[] { "", html.Replace("ca7Body", "changed-body"), html.Replace(names[0], "Unknown mission") })
            {
                try { PrimaryMissionCards.Parse(broken, DateTimeOffset.UtcNow); }
                catch (InvalidDataException) { continue; }
                throw new Exception("Changed or incomplete source accepted");
            }
        });
        test("Card download is asynchronous, retries failures, shares successful requests and supports cancellation", () =>
        {
            async Task Run()
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var handler = new Handler(async (count, token) =>
                {
                    await release.Task.WaitAsync(token);
                    return new(count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(html) };
                });
                using var http = new HttpClient(handler);
                var client = new PrimaryMissionCards(http);
                var first = client.GetAsync(names[0]); Check(!first.IsCompleted, "Download blocked caller");
                release.SetResult();
                try { await first; throw new Exception("HTTP failure hidden"); } catch (HttpRequestException) { }
                var cards = await Task.WhenAll(names.Select(n => client.GetAsync(n)));
                Check(cards.Length == 25 && handler.Count == 2, "Concurrent loads were not cached or failure prevented retry");
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { await client.GetAsync(names[0], cancellation.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { }
                try { await client.GetAsync("Unknown"); throw new Exception("Unknown card accepted"); }
                catch (InvalidDataException) { }
            }
            Run().GetAwaiter().GetResult();
        });
        var index = Array.IndexOf(args, "--primary-card-source");
        if (index >= 0 && index + 1 < args.Length)
            test("Published source supplies all 25 mapped primary cards with scoring text", () =>
            {
                var cards = PrimaryMissionCards.Parse(File.ReadAllText(args[index + 1]), DateTimeOffset.UtcNow);
                Check(names.All(n => cards.ContainsKey(UnitIdentity.Normalize(n))), "Published source is missing a mapped mission");
                Check(cards.Values.All(c => c.Text.Contains("VP") && !c.Text.Contains("<div")), "Unreadable live scoring text");
                Console.WriteLine("Verified " + cards.Count + " published cards; text lengths " + cards.Values.Min(c => c.Text.Length) + "–" + cards.Values.Max(c => c.Text.Length) + " characters.");
            });
    }
    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(Interlocked.Increment(ref Count), cancellationToken);
    }
}

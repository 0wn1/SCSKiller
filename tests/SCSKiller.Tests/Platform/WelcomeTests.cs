using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

/// <summary>The first-run welcome text: the bundled default, validation of the server's untrusted copy, .com then .io.</summary>
[Collection(TimingCollection.Name)]   // the budget tests: a starved thread pool reaches the second route after the budget
public class WelcomeTests
{
    static readonly string Valid = new StreamReader(typeof(WelcomeContent).Assembly.GetManifestResourceStream("SCSKiller.Core.App.welcome.json")!).ReadToEnd();

    static string With(Action<JsonObject> edit)
    {
        var o = JsonNode.Parse(Valid)!.AsObject();
        edit(o);
        return o.ToJsonString();
    }

    static string LinkTo(string url) => With(o => o["link"] = new JsonObject { ["text"] = "x", ["url"] = url });

    [Fact]
    public void Bundled_default_is_valid_and_the_same_bytes_the_server_serves()
    {
        Assert.Equal("Welcome to SCSKiller", WelcomeContent.Default.Title);
        Assert.Equal(3, WelcomeContent.Default.Paragraphs.Count);
        Assert.Equal("Sign in with Patreon", WelcomeContent.Default.SignIn);
        Assert.StartsWith("Help improve the community shader hash database", WelcomeContent.Default.Share);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "SCSKiller.slnx"))) root = root.Parent!;
        var served = Path.Combine(root.FullName, "server-rs", "content", "welcome.json");
        if (!Directory.Exists(Path.Combine(root.FullName, "server-rs"))) return;   // the public source has no server-rs/
        Assert.Equal(File.ReadAllBytes(Path.Combine(root.FullName, "src", "SCSKiller.Core", "App", "welcome.json")),
                     File.ReadAllBytes(served));
    }

    [Theory]
    [InlineData("https://scskiller.com/support")]
    [InlineData("https://www.scskiller.io/a?b=c")]
    [InlineData("https://beta.scskiller.xyz/")]
    [InlineData("https://www.patreon.com/scskiller")]
    [InlineData("https://patreon.com/scskiller")]
    public void Accepts_https_links_to_our_domains_and_patreon(string url) => Assert.NotNull(WelcomeContent.Parse(LinkTo(url)));

    [Fact]
    public void Accepts_no_link() => Assert.Null(WelcomeContent.Parse(With(o => o["link"] = null))!.Link);

    [Theory]
    [InlineData("http://scskiller.com/support")]            // not https
    [InlineData("https://evil.com/")]
    [InlineData("https://scskiller.com.evil.com/")]
    [InlineData("https://evilscskiller.com/")]
    [InlineData("https://scskiller.com@evil.com/")]
    [InlineData("https://evil.com@scskiller.com/")]         // user info
    [InlineData("https://scskiller.com:8443/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/support")]
    [InlineData("")]
    public void Rejects_other_links(string url) => Assert.Null(WelcomeContent.Parse(LinkTo(url)));

    [Fact]
    public void Rejects_over_long_text_missing_fields_and_bad_json()
    {
        Assert.NotNull(WelcomeContent.Parse(Valid));
        Assert.Null(WelcomeContent.Parse(With(o => o["title"] = new string('x', 101))));
        Assert.NotNull(WelcomeContent.Parse(With(o => o["title"] = new string('x', 100))));
        Assert.Null(WelcomeContent.Parse(With(o => o["paragraphs"] = new JsonArray(Enumerable.Repeat("p", 9).Select(p => (JsonNode?)p).ToArray()))));
        Assert.Null(WelcomeContent.Parse(With(o => o["paragraphs"] = new JsonArray(new string('x', 1001)))));
        Assert.Null(WelcomeContent.Parse(With(o => o["paragraphs"] = new JsonArray())));
        Assert.Null(WelcomeContent.Parse(With(o => o["paragraphs"] = new JsonArray("ok", null))));
        Assert.Null(WelcomeContent.Parse(With(o => o["signIn"] = new string('x', 41))));
        Assert.Null(WelcomeContent.Parse(With(o => o["share"] = new string('x', 151))));
        Assert.NotNull(WelcomeContent.Parse(With(o => o["share"] = new string('x', 150))));
        Assert.Null(WelcomeContent.Parse(With(o => o["link"] = new JsonObject { ["text"] = new string('x', 101), ["url"] = "https://scskiller.com/" })));
        foreach (var field in new[] { "title", "paragraphs", "share", "signIn", "dismiss" })
            Assert.Null(WelcomeContent.Parse(With(o => o.Remove(field))));
        Assert.Null(WelcomeContent.Parse(With(o => o["title"] = " ")));
        Assert.Null(WelcomeContent.Parse(With(o => o["link"] = new JsonObject { ["text"] = "x" })));   // no url
        Assert.Null(WelcomeContent.Parse(With(o => o["title"] = 5)));
        foreach (var bad in new[] { "", "null", "[]", "{", "<html>blocked</html>", Valid[..^10] })
            Assert.Null(WelcomeContent.Parse(bad));
    }

    sealed class Server(Func<string, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public readonly List<string> Asked = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            return answer(url, ct);
        }
    }

    static Task<HttpResponseMessage> Ok(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    const string Com = "https://api.scskiller.com/v1/content/welcome.json", Io = "https://api.scskiller.io/v1/content/welcome.json";
    static string Titled(string t) => With(o => o["title"] = t);
    static RouteFailover Via(Server s) => new(s, RouteFailover.DefaultRoutes);

    [Fact]
    public async Task Fetch_asks_com_and_stops_there_when_it_answers()
    {
        var s = new Server((_, _) => Ok(Titled("from com")));
        Assert.Equal("from com", (await WelcomeContent.FetchAsync(Via(s)))!.Title);
        Assert.Equal([Com], s.Asked);

        s = new Server((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));   // an answer, not a connect error
        Assert.Null(await WelcomeContent.FetchAsync(Via(s)));
        Assert.Equal([Com], s.Asked);

        s = new Server((_, _) => Ok(LinkTo("https://evil.com/")));   // invalid: rejected, no fallback
        Assert.Null(await WelcomeContent.FetchAsync(Via(s)));
        Assert.Equal([Com], s.Asked);
    }

    [Fact]
    public async Task Fetch_falls_back_to_io_on_a_connect_error_or_timeout()
    {
        var s = new Server((url, _) => url == Com ? throw new HttpRequestException("connection refused") : Ok(Titled("from io")));
        Assert.Equal("from io", (await WelcomeContent.FetchAsync(Via(s)))!.Title);
        Assert.Equal([Com, Io], s.Asked);

        s = new Server((url, _) => url == Com ? throw new TaskCanceledException("timed out", new TimeoutException()) : Ok(Titled("from io")));
        Assert.Equal("from io", (await WelcomeContent.FetchAsync(Via(s)))!.Title);
        Assert.Equal([Com, Io], s.Asked);

        s = new Server((_, _) => throw new HttpRequestException("no network"));
        Assert.Null(await WelcomeContent.FetchAsync(Via(s)));
        Assert.Equal([Com, Io], s.Asked);
    }

    [Fact]
    public async Task Fetch_gives_up_when_the_budget_runs_out()
    {
        var s = new Server(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });   // neither route answers
        var clock = Stopwatch.StartNew();
        Assert.Null(await WelcomeContent.FetchAsync(Via(s), TimeSpan.FromMilliseconds(200)));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), clock.Elapsed.ToString());
        Assert.Equal([Com, Io], s.Asked);   // .com had half the budget, .io the rest
    }

    [Fact]
    public async Task Fetch_with_com_blackholed_still_gets_io_within_the_3_s_budget()
    {
        var s = new Server(async (url, ct) =>
        {
            if (url == Com) await Task.Delay(Timeout.Infinite, ct);   // dropped SYNs: no error, just silence
            return await Ok(Titled("from io"));
        });
        var clock = Stopwatch.StartNew();
        Assert.Equal("from io", (await WelcomeContent.FetchAsync(Via(s)))!.Title);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), clock.Elapsed.ToString());
        Assert.Equal([Com, Io], s.Asked);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RewardQueue.Tests
{
    internal sealed class FakeQueueApi : IQueueApi
    {
        public string State = """{"type":"state","entries":[],"rewards":[]}""";
        public List<string> Removed { get; } = new List<string>();
        public List<string> Started { get; } = new List<string>();
        public List<string> Released { get; } = new List<string>();
        public List<(string Title, string? User, string? Input)> Added { get; } = new();
        public List<(string? RewardId, string? QueueId)> AddedTo { get; } = new();
        public HashSet<string> KnownIds { get; } = new HashSet<string> { "known" };

        public string BuildState() => State;

        public bool Remove(string id)
        {
            Removed.Add(id);
            return KnownIds.Contains(id);
        }

        public bool Start(string id)
        {
            Started.Add(id);
            return KnownIds.Contains(id);
        }

        public bool Release(string id)
        {
            Released.Add(id);
            return KnownIds.Contains(id);
        }

        public string? AddManual(string title, string? user, string? input, string? rewardId, string? queueId)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            Added.Add((title, user, input));
            AddedTo.Add((rewardId, queueId));
            return "manual-id";
        }
    }

    public class LocalServerTests : IClassFixture<PluginFixture>, IDisposable
    {
        private readonly FakeQueueApi _api = new FakeQueueApi();
        private readonly LocalServer _server;
        private readonly HttpClient _http = new HttpClient();
        private readonly int _port;
        private readonly string _key = ServerGuard.NewKey();

        public LocalServerTests(PluginFixture fixture)
        {
            _ = fixture;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _server = new LocalServer(_port, _key, _api);
            _server.Start();
        }

        public void Dispose()
        {
            _http.Dispose();
            _server.Dispose();
        }

        private string Url(string path, string extra = "") => $"http://127.0.0.1:{_port}{path}?key={_key}{extra}";

        private Task<HttpResponseMessage> Post(string path, string extra = "", string? body = null, bool withOrigin = true)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Url(path, extra));
            if (withOrigin) request.Headers.Add("Origin", $"http://127.0.0.1:{_port}");
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return _http.SendAsync(request);
        }

        [Theory]
        [InlineData("/panel")]
        [InlineData("/overlay")]
        public async Task Pages_AreServedWithTheKey(string path)
        {
            var response = await _http.GetAsync(Url(path));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("<html", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Requests_WithoutKeyOrWithWrongKeyOrForeignHostOrOriginAreRefused()
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync($"http://127.0.0.1:{_port}/panel")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _http.GetAsync($"http://127.0.0.1:{_port}/panel?key=wrong")).StatusCode);

            var foreignHost = new HttpRequestMessage(HttpMethod.Get, Url("/panel"));
            foreignHost.Headers.Host = "evil.example:" + _port;
            Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(foreignHost)).StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/done", "&id=known", withOrigin: false)).StatusCode);
            var foreignOrigin = new HttpRequestMessage(HttpMethod.Post, Url("/api/done", "&id=known"));
            foreignOrigin.Headers.Add("Origin", "http://evil.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(foreignOrigin)).StatusCode);

            Assert.Empty(_api.Removed);
        }

        [Fact]
        public async Task UnknownPath_Is404()
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync(Url("/nothing"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Post("/api/nothing")).StatusCode);
        }

        [Theory]
        [InlineData("/api/done")]
        [InlineData("/api/remove")]
        public async Task DoneAndRemove_TakeTheEntryOut(string path)
        {
            var response = await Post(path, "&id=known");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(new[] { "known" }, _api.Removed.ToArray());
        }

        [Fact]
        public async Task Start_AndRelease_PassTheIdToTheQueue()
        {
            Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/start", "&id=known")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await Post("/api/release", "&id=known")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Post("/api/start", "&id=нет-такой")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Post("/api/release")).StatusCode);

            Assert.Equal(new[] { "known", "нет-такой" }, _api.Started.ToArray());
            Assert.Equal(new[] { "known" }, _api.Released.ToArray());
        }

        [Fact]
        public async Task Start_RequiresKeyAndOrigin()
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/start", "&id=known", withOrigin: false)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _http.PostAsync($"http://127.0.0.1:{_port}/api/start?id=known", null)).StatusCode);
            Assert.Empty(_api.Started);
        }

        [Fact]
        public async Task DoneForUnknownOrMissingId_Is404()
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Post("/api/done", "&id=нет-такой")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await Post("/api/done")).StatusCode);
        }

        [Fact]
        public async Task Add_CreatesEntryAndReturnsItsId()
        {
            var response = await Post("/api/add", body: """{"title":"Заказ музыки","user":"Ник","input":"трек"}""");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("manual-id", json.RootElement.GetProperty("id").GetString());
            Assert.Equal(("Заказ музыки", "Ник", "трек"), _api.Added.Single());
        }

        [Fact]
        public async Task Add_PassesTheRewardAndTheQueueToTheApi()
        {
            await Post("/api/add", body: """{"title":"Музыка","reward_id":"r1","queue_id":"q2"}""");
            await Post("/api/add", body: """{"title":"Своё"}""");

            Assert.Equal(new (string?, string?)[] { ("r1", "q2"), ("", "") }, _api.AddedTo.ToArray());
        }

        [Theory]
        [InlineData("не json")]
        [InlineData("{}")]
        [InlineData("""{"title":"   "}""")]
        [InlineData("")]
        public async Task Add_WithoutTitleOrWithBrokenBody_Is400(string body)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/add", body: body)).StatusCode);
            Assert.Empty(_api.Added);
        }

        [Fact]
        public async Task Add_TooLargeBody_Is413()
        {
            string body = "{\"title\":\"" + new string('x', 20_000) + "\"}";

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Post("/api/add", body: body)).StatusCode);
            Assert.Empty(_api.Added);
        }

        // Читает события потока SSE
        private static async Task<string> NextEvent(StreamReader reader, int timeoutMs = 5000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            while (true)
            {
                string? line = await reader.ReadLineAsync(cts.Token);
                Assert.NotNull(line);
                if (line!.StartsWith("data: ")) return line[6..];
            }
        }

        [Fact]
        public async Task Events_SendCurrentStateAtOnceAndEveryPush()
        {
            _api.State = """{"type":"state","entries":[{"id":"a"}],"rewards":[]}""";
            using var response = await _http.GetAsync(Url("/events"), HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(_api.State, await NextEvent(reader));

            _api.State = """{"type":"state","entries":[],"rewards":[]}""";
            _server.PushState();
            Assert.Equal(_api.State, await NextEvent(reader));
        }

        [Fact]
        public async Task Events_CountOpenPagesAndForgetClosedOnes()
        {
            Assert.Equal(0, _server.ClientCount);
            var response = await _http.GetAsync(Url("/events"), HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            await NextEvent(reader);
            Assert.Equal(1, _server.ClientCount);

            response.Dispose();   // страница закрылась

            // сервер замечает это при следующей записи
            for (int i = 0; i < 100 && _server.ClientCount > 0; i++)
            {
                _server.PushState();
                await Task.Delay(50);
            }
            Assert.Equal(0, _server.ClientCount);
        }

        [Fact]
        public async Task PushState_WithoutPages_DoesNothing()
        {
            _server.PushState();
            Assert.Equal(0, _server.ClientCount);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task Server_StopsAnsweringAfterDispose()
        {
            _server.Dispose();
            await Task.Delay(200);

            await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync(Url("/panel")));
        }
    }

    public class QueueApiTests
    {
        private static QueueEntry Entry(string id, string title, string user = "Ник", string input = "") => new QueueEntry
        {
            id = id,
            reward_id = "r-" + title,
            reward_title = title,
            user_name = user,
            user_input = input,
            redeemed_at = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero)
        };

        [Fact]
        public void BuildStateJson_IsOneLineWithEntriesAndOnlyTrackedRewards()
        {
            var rewards = RewardSettings.Empty
                .WithKnown(new[] { new RewardInfo { id = "a", title = "Заказ техники" }, new RewardInfo { id = "b", title = "Заказ музыки" } })
                .WithTracked("a", true);

            string json = QueueApi.BuildStateJson(new[] { Entry("1", "Заказ техники", input: "строка 1\nстрока 2") }, rewards);

            Assert.DoesNotContain('\n', json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("state", root.GetProperty("type").GetString());
            var entry = root.GetProperty("entries")[0];
            Assert.Equal("1", entry.GetProperty("id").GetString());
            Assert.Equal("Заказ техники", entry.GetProperty("reward_title").GetString());
            Assert.Equal("Ник", entry.GetProperty("user_name").GetString());
            Assert.Equal("строка 1\nстрока 2", entry.GetProperty("user_input").GetString());
            Assert.Equal(new[] { "a" }, root.GetProperty("rewards").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToArray());
        }

        [Fact]
        public void BuildStateJson_CarriesQueuesWithNamesLimitsAndTotal()
        {
            var rewards = RewardSettings.Empty
                .WithKnown(new[] { new RewardInfo { id = "a", title = "Техника" }, new RewardInfo { id = "b", title = "Музыка" } })
                .WithTracked("a", true).WithTracked("b", true).AddQueue();
            string second = rewards.Queues[1].id;
            rewards = rewards.MoveReward("b", second).RenameQueue(second, "Музыка").SetShowLimit(second, 3).WithShowTotal(7);

            using var doc = JsonDocument.Parse(QueueApi.BuildStateJson(Array.Empty<QueueEntry>(), rewards));
            var root = doc.RootElement;

            var queues = root.GetProperty("queues");
            Assert.Equal(2, queues.GetArrayLength());
            Assert.Equal("", queues[0].GetProperty("name").GetString());
            Assert.Equal(0, queues[0].GetProperty("show_limit").GetInt32());
            Assert.Equal("Музыка", queues[1].GetProperty("name").GetString());
            Assert.Equal(3, queues[1].GetProperty("show_limit").GetInt32());
            Assert.Equal(second, queues[1].GetProperty("id").GetString());
            Assert.Equal(new[] { "b" }, queues[1].GetProperty("reward_ids").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal(7, root.GetProperty("show_total").GetInt32());
        }

        [Fact]
        public void BuildStateJson_EmptyQueue()
        {
            using var doc = JsonDocument.Parse(QueueApi.BuildStateJson(Array.Empty<QueueEntry>(), RewardSettings.Empty));

            Assert.Equal(0, doc.RootElement.GetProperty("entries").GetArrayLength());
            Assert.Equal(0, doc.RootElement.GetProperty("rewards").GetArrayLength());
        }

        [Fact]
        public void BuildStateJson_TextsAreEscapedSoPagesCanTrustTheJson()
        {
            string json = QueueApi.BuildStateJson(new[] { Entry("1", "A", user: "<script>alert(1)</script>", input: "\"кавычки\" и \\ слэш") }, RewardSettings.Empty);

            using var doc = JsonDocument.Parse(json);
            var entry = doc.RootElement.GetProperty("entries")[0];
            Assert.Equal("<script>alert(1)</script>", entry.GetProperty("user_name").GetString());
            Assert.Equal("\"кавычки\" и \\ слэш", entry.GetProperty("user_input").GetString());
        }

        [Fact]
        public void ParseAddRequest_ReadsFieldsAndRejectsGarbage()
        {
            var ok = QueueApi.ParseAddRequest("""{"title":"Музыка","user":"Ник","input":"трек"}""");
            Assert.Equal("Музыка", ok!.title);
            Assert.Equal("Ник", ok.user);
            Assert.Equal("трек", ok.input);

            Assert.Null(QueueApi.ParseAddRequest("не json"));
            Assert.Null(QueueApi.ParseAddRequest("{}"));
            Assert.Null(QueueApi.ParseAddRequest("""{"title":""}"""));
            Assert.Null(QueueApi.ParseAddRequest("null"));
        }
    }
}

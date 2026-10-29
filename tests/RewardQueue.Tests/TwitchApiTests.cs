using System.Text.Json;
using Xunit;

namespace RewardQueue.Tests
{
    public class TwitchApiTests : IClassFixture<PluginFixture>
    {
        private const string ClientId = "g1e25xwrf3oqosokb3r98vpqhhj0pd";

        private readonly string _vault;
        private readonly FakeTwitchHandler _authNet = new FakeTwitchHandler();
        private readonly FakeTwitchHandler _apiNet = new FakeTwitchHandler();
        private readonly DateTimeOffset _now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        public TwitchApiTests(PluginFixture fixture)
        {
            _vault = Path.Combine(fixture.Dir, "api_auth_" + Guid.NewGuid().ToString("N") + ".bin");
        }

        // Вошедший пользователь: токен лежит в хранилище, проверка у «Twitch» проходит
        private async Task<TwitchAuth> SignedIn()
        {
            TokenVault.Save(_vault, new StoredAuth
            {
                client_id = ClientId,
                access_token = "at-1",
                refresh_token = "rt-1",
                expires_at = _now.AddHours(3),
                user_id = "12345",
                login = "streamer",
                scopes = new[] { "channel:read:redemptions" }
            });
            _authNet.Responder = (path, body) => path switch
            {
                "/oauth2/validate" => (200, $$"""{"client_id":"{{ClientId}}","login":"streamer","scopes":["channel:read:redemptions"],"user_id":"12345","expires_in":14000}"""),
                "/oauth2/token" => (200, """{"access_token":"at-2","expires_in":14400,"refresh_token":"rt-2","scope":["channel:read:redemptions"],"token_type":"bearer"}"""),
                _ => (404, "")
            };
            var auth = new TwitchAuth(_vault, ClientId, _authNet, () => _now);
            await auth.InitializeAsync();
            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
            return auth;
        }

        private static string RewardsJson(params (string Id, string Title)[] rewards) =>
            "{\"data\":[" + string.Join(",", rewards.Select(r => $$"""{"id":"{{r.Id}}","title":"{{r.Title}}","cost":100,"is_enabled":true}""")) + "]}";

        [Fact]
        public async Task GetRewards_ReturnsRewardsSortedByTitle()
        {
            using var auth = await SignedIn();
            _apiNet.Responder = (path, body) => (200, RewardsJson(("b", "Заказ музыки"), ("a", "Заказ техники"), ("c", "Автограф")));
            using var api = new TwitchApi(auth, _apiNet);

            var result = await api.GetRewardsAsync();

            Assert.Equal(RewardsResultKind.Success, result.Kind);
            Assert.Equal(new[] { "Автограф", "Заказ музыки", "Заказ техники" }, result.Rewards.Select(r => r.title).ToArray());
            Assert.Equal("c", result.Rewards[0].id);
        }

        [Fact]
        public async Task GetRewards_SendsTokenClientIdAndBroadcasterId()
        {
            using var auth = await SignedIn();
            _apiNet.Responder = (path, body) => (200, RewardsJson());
            using var api = new TwitchApi(auth, _apiNet);

            await api.GetRewardsAsync();

            var request = _apiNet.Requests.Single();
            Assert.Equal("/helix/channel_points/custom_rewards", request.Path);
            Assert.Equal("Bearer at-1", request.Authorization);
        }

        [Fact]
        public async Task GetRewards_AsksForTheBroadcasterOfTheLogin()
        {
            using var auth = await SignedIn();
            string? seenQuery = null;
            var handler = new QueryCapturingHandler(RewardsJson(), q => seenQuery = q);
            using var api = new TwitchApi(auth, handler);

            await api.GetRewardsAsync();

            Assert.Equal("?broadcaster_id=12345", seenQuery);
            Assert.Equal(ClientId, handler.ClientIdHeader);
        }

        [Fact]
        public async Task GetRewards_WithoutLoginSaysSoWithoutNetwork()
        {
            using var auth = new TwitchAuth(_vault, ClientId, _authNet, () => _now);
            using var api = new TwitchApi(auth, _apiNet);

            var result = await api.GetRewardsAsync();

            Assert.Equal(RewardsResultKind.NotSignedIn, result.Kind);
            Assert.Empty(_apiNet.Requests);
        }

        [Fact]
        public async Task GetRewards_RejectedTokenIsRefreshedAndRequestRepeatedOnce()
        {
            using var auth = await SignedIn();
            // Twitch перестал признавать токен: проверка входа тоже отвечает 401, после чего токен обновляется
            int validates = 0;
            _authNet.Responder = (path, body) => path switch
            {
                "/oauth2/validate" => ++validates == 1
                    ? (401, """{"status":401,"message":"invalid access token"}""")
                    : (200, $$"""{"client_id":"{{ClientId}}","login":"streamer","scopes":["channel:read:redemptions"],"user_id":"12345","expires_in":14000}"""),
                "/oauth2/token" => (200, """{"access_token":"at-2","expires_in":14400,"refresh_token":"rt-2","scope":["channel:read:redemptions"],"token_type":"bearer"}"""),
                _ => (404, "")
            };
            int calls = 0;
            _apiNet.Responder = (path, body) => ++calls == 1 ? (401, """{"status":401,"message":"Invalid OAuth token"}""") : (200, RewardsJson(("a", "A")));
            using var api = new TwitchApi(auth, _apiNet);

            var result = await api.GetRewardsAsync();

            Assert.Equal(RewardsResultKind.Success, result.Kind);
            Assert.Equal(2, _apiNet.Requests.Count);
            Assert.Equal("Bearer at-1", _apiNet.Requests[0].Authorization);
            Assert.Equal("Bearer at-2", _apiNet.Requests[1].Authorization);   // токен обновился между попытками
        }

        [Fact]
        public async Task GetRewards_StillRejectedAfterRefreshIsUnauthorized()
        {
            using var auth = await SignedIn();
            _apiNet.Responder = (path, body) => (401, """{"status":401,"message":"Invalid OAuth token"}""");
            using var api = new TwitchApi(auth, _apiNet);

            var result = await api.GetRewardsAsync();

            Assert.Equal(RewardsResultKind.Unauthorized, result.Kind);
            Assert.Equal(2, _apiNet.Requests.Count);
        }

        [Fact]
        public async Task GetRewards_ChannelWithoutAffiliateStatusIsForbidden()
        {
            using var auth = await SignedIn();
            _apiNet.Responder = (path, body) => (403, """{"status":403,"error":"Forbidden","message":"The broadcaster must have partner or affiliate status."}""");
            using var api = new TwitchApi(auth, _apiNet);

            var result = await api.GetRewardsAsync();

            Assert.Equal(RewardsResultKind.Forbidden, result.Kind);
            Assert.Contains("affiliate", result.Message);
        }

        [Fact]
        public async Task GetRewards_ServerErrorAndNetworkFailure()
        {
            using var auth = await SignedIn();
            using var api = new TwitchApi(auth, _apiNet);

            _apiNet.Responder = (path, body) => (500, """{"status":500,"message":"сбой"}""");
            Assert.Equal(RewardsResultKind.Failed, (await api.GetRewardsAsync()).Kind);

            _apiNet.Responder = (path, body) => throw new HttpRequestException("нет сети");
            Assert.Equal(RewardsResultKind.Offline, (await api.GetRewardsAsync()).Kind);

            _apiNet.Responder = (path, body) => (200, "не json");
            Assert.Equal(RewardsResultKind.Failed, (await api.GetRewardsAsync()).Kind);
        }

        [Fact]
        public async Task Subscribe_SendsTheRedemptionSubscriptionForTheSession()
        {
            using var auth = await SignedIn();
            _apiNet.Responder = (path, body) => (202, """{"data":[{"id":"sub"}]}""");
            using var api = new TwitchApi(auth, _apiNet);

            var result = await api.SubscribeRedemptionsAsync("session-42", CancellationToken.None);

            Assert.Equal(SubscribeResult.Success, result);
            var request = _apiNet.Requests.Single();
            Assert.Equal("/helix/eventsub/subscriptions", request.Path);
            Assert.Equal("Bearer at-1", request.Authorization);

            using var json = JsonDocument.Parse(request.Body);
            var root = json.RootElement;
            Assert.Equal("channel.channel_points_custom_reward_redemption.add", root.GetProperty("type").GetString());
            Assert.Equal("1", root.GetProperty("version").GetString());
            // одна подписка на канал без reward_id: награды выбираем у себя
            Assert.Equal("12345", root.GetProperty("condition").GetProperty("broadcaster_user_id").GetString());
            Assert.False(root.GetProperty("condition").TryGetProperty("reward_id", out _));
            Assert.Equal("websocket", root.GetProperty("transport").GetProperty("method").GetString());
            Assert.Equal("session-42", root.GetProperty("transport").GetProperty("session_id").GetString());
        }

        [Fact]
        public async Task Subscribe_RejectionsAndFailures()
        {
            using var auth = await SignedIn();
            using var api = new TwitchApi(auth, _apiNet);

            _apiNet.Responder = (path, body) => (401, "");
            Assert.Equal(SubscribeResult.Unauthorized, await api.SubscribeRedemptionsAsync("s", CancellationToken.None));

            _apiNet.Responder = (path, body) => (400, """{"status":400,"message":"session is not connected"}""");
            Assert.Equal(SubscribeResult.Failed, await api.SubscribeRedemptionsAsync("s", CancellationToken.None));

            _apiNet.Responder = (path, body) => (429, "");
            Assert.Equal(SubscribeResult.Failed, await api.SubscribeRedemptionsAsync("s", CancellationToken.None));
        }

        [Fact]
        public async Task Subscribe_WithoutLoginIsUnauthorizedWithoutNetwork()
        {
            using var auth = new TwitchAuth(_vault, ClientId, _authNet, () => _now);
            using var api = new TwitchApi(auth, _apiNet);

            Assert.Equal(SubscribeResult.Unauthorized, await api.SubscribeRedemptionsAsync("s", CancellationToken.None));
            Assert.Empty(_apiNet.Requests);
        }

        // Запоминает строку запроса и заголовок Client-Id (FakeTwitchHandler их не сохраняет)
        private sealed class QueryCapturingHandler : HttpMessageHandler
        {
            private readonly string _json;
            private readonly Action<string> _onQuery;

            public QueryCapturingHandler(string json, Action<string> onQuery)
            {
                _json = json;
                _onQuery = onQuery;
            }

            public string? ClientIdHeader { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _onQuery(request.RequestUri!.Query);
                ClientIdHeader = request.Headers.TryGetValues("Client-Id", out var values) ? values.First() : null;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
                });
            }
        }
    }
}

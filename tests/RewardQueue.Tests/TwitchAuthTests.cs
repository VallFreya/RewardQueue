using System.Net;
using System.Text;
using Xunit;

namespace RewardQueue.Tests
{
    // Подмена сети: каждый запрос попадает в Responder, который по пути и телу отвечает или бросает HttpRequestException
    internal sealed class FakeTwitchHandler : HttpMessageHandler
    {
        public delegate (int Status, string Body) ResponderFunc(string path, string body);

        public ResponderFunc Responder { get; set; } = (path, body) => (404, "");
        public List<(string Path, string Body, string? Authorization)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string? auth = request.Headers.TryGetValues("Authorization", out var values) ? values.First() : null;
            string path = request.RequestUri!.AbsolutePath;
            lock (Requests) Requests.Add((path, body, auth));

            var (status, json) = Responder(path, body);
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        public int Count(string path, string? bodyPart = null)
        {
            lock (Requests) return Requests.Count(r => r.Path == path && (bodyPart == null || r.Body.Contains(bodyPart)));
        }
    }

    public class TwitchAuthTests : IClassFixture<PluginFixture>
    {
        private const string ClientId = "g1e25xwrf3oqosokb3r98vpqhhj0pd";
        private const string DeviceJson = """{"device_code":"dc123","expires_in":1800,"interval":5,"user_code":"ABCDEFGH","verification_uri":"https://www.twitch.tv/activate?device-code=ABCDEFGH"}""";
        private const string Pending = """{"status":400,"message":"authorization_pending"}""";

        private readonly string _vault;
        private readonly FakeTwitchHandler _net = new FakeTwitchHandler();
        private DateTimeOffset _now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        private readonly List<TimeSpan> _delays = new List<TimeSpan>();

        public TwitchAuthTests(PluginFixture fixture)
        {
            _vault = Path.Combine(fixture.Dir, "auth_" + Guid.NewGuid().ToString("N") + ".bin");
        }

        private TwitchAuth NewAuth(string? clientId = null) => new TwitchAuth(_vault, clientId ?? ClientId, _net, () => _now, (span, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            _delays.Add(span);
            _now += span;
            return Task.CompletedTask;
        });

        private static string TokensJson(string access = "at1", string refresh = "rt1", string scope = "\"channel:read:redemptions\"") =>
            $$"""{"access_token":"{{access}}","expires_in":14400,"refresh_token":"{{refresh}}","scope":[{{scope}}],"token_type":"bearer"}""";

        private static string ValidateJson(string login = "streamer", string scope = "\"channel:read:redemptions\"") =>
            $$"""{"client_id":"{{ClientId}}","login":"{{login}}","scopes":[{{scope}}],"user_id":"12345","expires_in":14000}""";

        // Сервер Twitch, который подтверждает вход после заданного числа «ещё не подтвердил»
        private void ServeLogin(int pendingAnswers = 2, string? validate = null)
        {
            int polls = 0;
            _net.Responder = (path, body) => path switch
            {
                "/oauth2/device" => (200, DeviceJson),
                "/oauth2/token" when body.Contains("device_code") => ++polls <= pendingAnswers ? (400, Pending) : (200, TokensJson()),
                "/oauth2/validate" => (200, validate ?? ValidateJson()),
                _ => (404, "")
            };
        }

        private StoredAuth Stored(DateTimeOffset expiresAt, string access = "old-at", string refresh = "old-rt") => new StoredAuth
        {
            client_id = ClientId,
            access_token = access,
            refresh_token = refresh,
            expires_at = expiresAt,
            user_id = "12345",
            login = "streamer",
            scopes = new[] { "channel:read:redemptions" }
        };

        [Fact]
        public async Task Login_ShowsCodeThenSignsInAndRemembersTheLogin()
        {
            ServeLogin(pendingAnswers: 2);
            using var auth = NewAuth();
            var states = new List<AuthSnapshot>();
            auth.Changed += () => states.Add(auth.Snapshot);

            await auth.LoginAsync();

            Assert.Contains(states, s => s.State == AuthState.WaitingForUser && s.UserCode == "ABCDEFGH"
                && s.VerificationUri.Contains("device-code=ABCDEFGH"));
            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
            Assert.Equal("streamer", auth.Snapshot.Login);
            Assert.Equal("12345", auth.Snapshot.UserId);
            Assert.Equal(3, _net.Count("/oauth2/token"));   // два «ещё нет» и один успех

            // после перезапуска плагина вход восстанавливается из файла
            using var restarted = NewAuth();
            await restarted.InitializeAsync();
            Assert.Equal(AuthState.SignedIn, restarted.Snapshot.State);
            Assert.Equal("streamer", restarted.Snapshot.Login);
        }

        [Fact]
        public async Task Login_AsksForTheRedemptionsScopeWithoutSecret()
        {
            ServeLogin(pendingAnswers: 0);
            using var auth = NewAuth();

            await auth.LoginAsync();

            var request = _net.Requests.First(r => r.Path == "/oauth2/device");
            Assert.Contains("client_id=" + ClientId, request.Body);
            Assert.Contains("channel%3Aread%3Aredemptions", request.Body);
            Assert.DoesNotContain("secret", string.Join("", _net.Requests.Select(r => r.Body)), StringComparison.OrdinalIgnoreCase);
            // access-токен передаётся только в заголовке проверки
            Assert.Equal("OAuth at1", _net.Requests.First(r => r.Path == "/oauth2/validate").Authorization);
        }

        [Fact]
        public async Task Login_TokensAreNotStoredAsPlainText()
        {
            ServeLogin(pendingAnswers: 0);
            using var auth = NewAuth();

            await auth.LoginAsync();

            byte[] file = File.ReadAllBytes(_vault);
            string text = Encoding.UTF8.GetString(file);
            Assert.DoesNotContain("at1", text);
            Assert.DoesNotContain("rt1", text);
            Assert.DoesNotContain("streamer", text);
        }

        [Fact]
        public async Task Login_SlowDownLengthensThePause()
        {
            int polls = 0;
            _net.Responder = (path, body) => path switch
            {
                "/oauth2/device" => (200, DeviceJson),
                "/oauth2/token" => ++polls == 1 ? (400, """{"status":400,"message":"slow_down"}""") : (200, TokensJson()),
                "/oauth2/validate" => (200, ValidateJson()),
                _ => (404, "")
            };
            using var auth = NewAuth();

            await auth.LoginAsync();

            Assert.Equal(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }, _delays);
        }

        [Fact]
        public async Task Login_DeniedInBrowserShowsProblem()
        {
            _net.Responder = (path, body) => path switch
            {
                "/oauth2/device" => (200, DeviceJson),
                "/oauth2/token" => (400, """{"status":400,"message":"access_denied"}"""),
                _ => (404, "")
            };
            using var auth = NewAuth();

            await auth.LoginAsync();

            Assert.Equal(AuthState.SignedOut, auth.Snapshot.State);
            Assert.Equal(AuthProblem.Denied, auth.Snapshot.Problem);
            Assert.False(File.Exists(_vault));
        }

        [Fact]
        public async Task Login_CodeExpiresWhenUserDoesNotConfirm()
        {
            _net.Responder = (path, body) => path switch
            {
                "/oauth2/device" => (200, DeviceJson.Replace("1800", "20")),
                "/oauth2/token" => (400, Pending),
                _ => (404, "")
            };
            using var auth = NewAuth();

            await auth.LoginAsync();

            Assert.Equal(AuthProblem.CodeExpired, auth.Snapshot.Problem);
            Assert.True(_delays.Count <= 5);   // пауза по 5 секунд, за 20 секунд код истёк
        }

        [Fact]
        public async Task Login_WithoutRedemptionsScopeIsRejected()
        {
            ServeLogin(pendingAnswers: 0, validate: ValidateJson(scope: "\"chat:read\""));
            using var auth = NewAuth();

            await auth.LoginAsync();

            Assert.Equal(AuthProblem.MissingScope, auth.Snapshot.Problem);
            Assert.Equal(AuthState.SignedOut, auth.Snapshot.State);
            Assert.False(File.Exists(_vault));
        }

        [Fact]
        public async Task Login_CanBeCancelledWhileWaiting()
        {
            var gate = new TaskCompletionSource();
            _net.Responder = (path, body) => path switch
            {
                "/oauth2/device" => (200, DeviceJson),
                "/oauth2/token" => (400, Pending),
                _ => (404, "")
            };
            // пауза, которая ждёт отмены
            using var auth = new TwitchAuth(_vault, ClientId, _net, () => _now, async (span, ct) =>
            {
                gate.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });

            var login = auth.LoginAsync();
            await gate.Task;
            Assert.Equal(AuthState.WaitingForUser, auth.Snapshot.State);

            auth.CancelLogin();
            await login;

            Assert.Equal(AuthState.SignedOut, auth.Snapshot.State);
            Assert.Equal(AuthProblem.None, auth.Snapshot.Problem);
        }

        [Fact]
        public async Task Login_NoConnectionGivesOfflineProblem()
        {
            _net.Responder = (path, body) => throw new HttpRequestException("нет сети");
            using var auth = NewAuth();

            await auth.LoginAsync();

            Assert.Equal(AuthProblem.Offline, auth.Snapshot.Problem);
        }

        [Fact]
        public async Task Login_ShortOutageWhilePollingDoesNotLoseTheLogin()
        {
            int polls = 0;
            _net.Responder = (path, body) =>
            {
                if (path == "/oauth2/device") return (200, DeviceJson);
                if (path == "/oauth2/validate") return (200, ValidateJson());
                if (++polls <= 2) throw new HttpRequestException("обрыв");
                return (200, TokensJson());
            };
            using var auth = NewAuth();

            await auth.LoginAsync();

            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
        }

        [Fact]
        public async Task Initialize_WithoutSavedLoginStaysSignedOut()
        {
            using var auth = NewAuth();

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedOut, auth.Snapshot.State);
            Assert.Empty(_net.Requests);
        }

        [Fact]
        public async Task Initialize_ValidatesSavedLoginWithoutRefreshingFreshToken()
        {
            TokenVault.Save(_vault, Stored(_now.AddHours(3)));
            _net.Responder = (path, body) => path == "/oauth2/validate" ? (200, ValidateJson()) : (404, "");
            using var auth = NewAuth();

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
            Assert.Equal(0, _net.Count("/oauth2/token"));
            Assert.Equal("OAuth old-at", _net.Requests.Single().Authorization);
        }

        [Fact]
        public async Task Initialize_RefreshesExpiredTokenAndStoresNewRefreshTokenFirst()
        {
            TokenVault.Save(_vault, Stored(_now.AddMinutes(-5)));
            string? storedRefreshAtValidate = null;
            _net.Responder = (path, body) =>
            {
                if (path == "/oauth2/token") return (200, TokensJson("new-at", "new-rt"));
                // к моменту проверки новый refresh-токен уже должен лежать на диске
                storedRefreshAtValidate = TokenVault.Load(_vault)?.refresh_token;
                return (200, ValidateJson());
            };
            using var auth = NewAuth();

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
            Assert.Equal("new-rt", storedRefreshAtValidate);
            Assert.Contains("refresh_token=old-rt", _net.Requests.First(r => r.Path == "/oauth2/token").Body);
            Assert.DoesNotContain("client_secret", _net.Requests.First(r => r.Path == "/oauth2/token").Body);
            Assert.Equal("OAuth new-at", _net.Requests.First(r => r.Path == "/oauth2/validate").Authorization);
        }

        [Fact]
        public async Task Initialize_RejectedRefreshTokenSignsOutAndForgetsTheFile()
        {
            TokenVault.Save(_vault, Stored(_now.AddMinutes(-5)));
            _net.Responder = (path, body) => (400, """{"status":400,"message":"Invalid refresh token"}""");
            using var auth = NewAuth();

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedOut, auth.Snapshot.State);
            Assert.Equal(AuthProblem.SessionExpired, auth.Snapshot.Problem);
            Assert.False(File.Exists(_vault));
        }

        [Fact]
        public async Task Initialize_TokenRejectedByValidateIsRefreshedOnce()
        {
            TokenVault.Save(_vault, Stored(_now.AddHours(3)));
            int validates = 0;
            _net.Responder = (path, body) =>
            {
                if (path == "/oauth2/token") return (200, TokensJson("fresh-at", "fresh-rt"));
                return ++validates == 1 ? (401, """{"status":401,"message":"invalid access token"}""") : (200, ValidateJson());
            };
            using var auth = NewAuth();

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
            Assert.Equal(1, _net.Count("/oauth2/token"));
            Assert.Equal("fresh-rt", TokenVault.Load(_vault)!.refresh_token);
        }

        [Fact]
        public async Task Initialize_NoConnectionKeepsTheLoginAndReportsOffline()
        {
            TokenVault.Save(_vault, Stored(_now.AddHours(3)));
            _net.Responder = (path, body) => throw new HttpRequestException("нет сети");
            using var auth = NewAuth();

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
            Assert.Equal(AuthProblem.Offline, auth.Snapshot.Problem);
            Assert.True(File.Exists(_vault));
        }

        [Fact]
        public async Task Revalidate_AfterOutageClearsTheProblem()
        {
            TokenVault.Save(_vault, Stored(_now.AddHours(3)));
            bool online = false;
            _net.Responder = (path, body) => online ? (200, ValidateJson()) : throw new HttpRequestException("нет сети");
            using var auth = NewAuth();
            await auth.InitializeAsync();
            Assert.Equal(AuthProblem.Offline, auth.Snapshot.Problem);

            online = true;
            await auth.RevalidateAsync();

            Assert.Equal(AuthProblem.None, auth.Snapshot.Problem);
        }

        [Fact]
        public async Task Initialize_LoginOfAnotherApplicationIsIgnored()
        {
            var other = Stored(_now.AddHours(3));
            other.client_id = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";
            TokenVault.Save(_vault, other);
            using var auth = NewAuth();

            await auth.InitializeAsync();

            Assert.Equal(AuthState.SignedOut, auth.Snapshot.State);
            Assert.False(File.Exists(_vault));
            Assert.Empty(_net.Requests);
        }

        [Fact]
        public async Task GetAccessToken_ReturnsFreshTokenWithoutNetwork()
        {
            TokenVault.Save(_vault, Stored(_now.AddHours(3)));
            _net.Responder = (path, body) => (200, ValidateJson());
            using var auth = NewAuth();
            await auth.InitializeAsync();
            int before = _net.Requests.Count;

            Assert.Equal("old-at", await auth.GetAccessTokenAsync());
            Assert.Equal(before, _net.Requests.Count);
        }

        [Fact]
        public async Task GetAccessToken_ConcurrentCallsRefreshOnce()
        {
            TokenVault.Save(_vault, Stored(_now.AddHours(3)));
            _net.Responder = (path, body) => path == "/oauth2/token" ? (200, TokensJson("new-at", "new-rt")) : (200, ValidateJson());
            using var auth = NewAuth();
            await auth.InitializeAsync();

            _now = _now.AddSeconds(14000).AddMinutes(-5);   // проверка выставила срок 14000 с; до конца токена меньше десяти минут
            var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => auth.GetAccessTokenAsync()));

            Assert.All(tokens, t => Assert.Equal("new-at", t));
            Assert.Equal(1, _net.Count("/oauth2/token"));
        }

        [Fact]
        public async Task GetAccessToken_NullWithoutLogin()
        {
            using var auth = NewAuth();
            Assert.Null(await auth.GetAccessTokenAsync());
        }

        [Fact]
        public async Task SignOut_ForgetsLoginAndRevokesTokenAtTwitch()
        {
            ServeLogin(pendingAnswers: 0);
            using var auth = NewAuth();
            await auth.LoginAsync();
            Assert.True(File.Exists(_vault));

            auth.SignOut();

            Assert.Equal(AuthState.SignedOut, auth.Snapshot.State);
            Assert.False(File.Exists(_vault));
            Assert.Null(await auth.GetAccessTokenAsync());
            for (int i = 0; i < 100 && _net.Count("/oauth2/revoke") == 0; i++) await Task.Delay(20);
            Assert.Equal(1, _net.Count("/oauth2/revoke"));
            Assert.Contains("token=at1", _net.Requests.First(r => r.Path == "/oauth2/revoke").Body);
        }

        [Fact]
        public async Task Login_IsIgnoredWhileAlreadySignedIn()
        {
            ServeLogin(pendingAnswers: 0);
            using var auth = NewAuth();
            await auth.LoginAsync();
            int requests = _net.Requests.Count;

            await auth.LoginAsync();

            Assert.Equal(requests, _net.Requests.Count);
            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
        }

        [Fact]
        public async Task FailingChangeHandlerDoesNotBreakLogin()
        {
            ServeLogin(pendingAnswers: 0);
            using var auth = NewAuth();
            auth.Changed += () => throw new InvalidOperationException("сбой подписчика");

            await auth.LoginAsync();

            Assert.Equal(AuthState.SignedIn, auth.Snapshot.State);
        }
    }

    public class TokenVaultTests : IClassFixture<PluginFixture>
    {
        private readonly string _path;

        public TokenVaultTests(PluginFixture fixture)
        {
            _path = Path.Combine(fixture.Dir, "vault_" + Guid.NewGuid().ToString("N") + ".bin");
        }

        [Fact]
        public void SaveThenLoad_RoundTripsAndEncrypts()
        {
            var auth = new StoredAuth
            {
                client_id = "c",
                access_token = "секретный-access",
                refresh_token = "секретный-refresh",
                expires_at = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero),
                user_id = "42",
                login = "streamer",
                scopes = new[] { "channel:read:redemptions" }
            };

            TokenVault.Save(_path, auth);
            var loaded = TokenVault.Load(_path)!;

            Assert.Equal(auth.access_token, loaded.access_token);
            Assert.Equal(auth.refresh_token, loaded.refresh_token);
            Assert.Equal(auth.expires_at, loaded.expires_at);
            Assert.Equal("streamer", loaded.login);
            Assert.DoesNotContain("секретный", Encoding.UTF8.GetString(File.ReadAllBytes(_path)));
            Assert.False(File.Exists(_path + ".tmp"));
        }

        [Fact]
        public void Load_MissingFileGivesNull()
        {
            Assert.Null(TokenVault.Load(_path));
        }

        [Theory]
        [InlineData("")]
        [InlineData("просто текст, не зашифрованный")]
        public void Load_DamagedFileGivesNull(string content)
        {
            File.WriteAllText(_path, content);

            Assert.Null(TokenVault.Load(_path));
        }

        [Fact]
        public void Delete_RemovesFileAndIgnoresMissingOne()
        {
            TokenVault.Save(_path, new StoredAuth { access_token = "a", refresh_token = "r" });

            TokenVault.Delete(_path);
            TokenVault.Delete(_path);

            Assert.False(File.Exists(_path));
        }
    }
}

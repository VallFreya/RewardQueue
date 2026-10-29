using System.Threading.Channels;
using Xunit;

namespace RewardQueue.Tests
{
    // Подмена сокета: тест кладёт в него сообщения, клиент читает их как с сервера
    internal sealed class FakeSocket : IEventSubSocket
    {
        private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
        public bool Closed { get; private set; }
        public bool Disposed { get; private set; }

        public void Push(string message) => _incoming.Writer.TryWrite(message);

        /// <summary>Сервер закрыл соединение</summary>
        public void ServerCloses() => _incoming.Writer.TryWrite(null);

        public async Task<string?> ReceiveAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);

        public Task CloseAsync()
        {
            Closed = true;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    public class EventSubClientTests : IClassFixture<PluginFixture>
    {
        private static readonly EventSubTimings Fast = new EventSubTimings(
            WelcomeTimeout: TimeSpan.FromMilliseconds(300),
            SubscribeTimeout: TimeSpan.FromMilliseconds(300),
            KeepaliveMargin: TimeSpan.FromMilliseconds(100),
            InitialBackoff: TimeSpan.FromSeconds(1),
            MaxBackoff: TimeSpan.FromSeconds(8));

        private readonly List<Uri> _connects = new List<Uri>();
        private readonly List<FakeSocket> _sockets = new List<FakeSocket>();
        private readonly List<string> _subscribedSessions = new List<string>();
        private readonly List<RedemptionEvent> _received = new List<RedemptionEvent>();
        private readonly List<TimeSpan> _delays = new List<TimeSpan>();
        private SubscribeResult _subscribeResult = SubscribeResult.Success;
        private int _unauthorizedCalls;
        private Action<RedemptionEvent> _onRedemption;

        public EventSubClientTests(PluginFixture fixture)
        {
            _ = fixture;
            _onRedemption = e => _received.Add(e);
        }

        // Каждое подключение получает следующий сокет из списка; тест сам кладёт в него приветствие
        private EventSubClient NewClient(Func<FakeSocket>? makeSocket = null) => new EventSubClient(
            connect: (uri, ct) =>
            {
                var socket = makeSocket?.Invoke() ?? new FakeSocket();
                lock (_sockets) { _connects.Add(uri); _sockets.Add(socket); }
                return Task.FromResult<IEventSubSocket>(socket);
            },
            subscribe: (session, ct) =>
            {
                lock (_subscribedSessions) _subscribedSessions.Add(session);
                return Task.FromResult(_subscribeResult);
            },
            onRedemption: e => _onRedemption(e),
            onUnauthorized: () => { Interlocked.Increment(ref _unauthorizedCalls); return Task.CompletedTask; },
            timings: Fast,
            delay: async (span, ct) =>
            {
                lock (_delays) _delays.Add(span);
                await Task.Delay(5, ct);
            });

        private static async Task WaitFor(Func<bool> condition, string what, int ms = 5000)
        {
            for (int i = 0; i < ms / 10 && !condition(); i++) await Task.Delay(10);
            Assert.True(condition(), "не дождались: " + what);
        }

        private FakeSocket Socket(int index)
        {
            lock (_sockets) return _sockets[index];
        }

        private int SocketCount()
        {
            lock (_sockets) return _sockets.Count;
        }

        private async Task<EventSubClient> ConnectedClient()
        {
            var client = NewClient();
            client.Start();
            await WaitFor(() => SocketCount() == 1, "первое подключение");
            Socket(0).Push(EventSubMessagesTests.Welcome);
            await WaitFor(() => client.State == EventSubState.Connected, "состояние Connected");
            return client;
        }

        [Fact]
        public async Task Connects_Subscribes_WithSessionIdFromWelcome_AndDeliversRedemptions()
        {
            var client = await ConnectedClient();

            Assert.Equal(new[] { "AQoQILE98gtqShGmLD7AM6yJThAB" }, _subscribedSessions);
            Assert.Equal(EventSubMessages.Endpoint, _connects[0].ToString());

            Socket(0).Push(EventSubMessagesTests.Keepalive);
            Socket(0).Push(EventSubMessagesTests.Notification("r1", user: "Первый"));
            Socket(0).Push(EventSubMessagesTests.Notification("r2", user: "Второй"));
            await WaitFor(() => _received.Count == 2, "два заказа");

            Assert.Equal(new[] { "r1", "r2" }, _received.Select(r => r.Id).ToArray());
            Assert.Equal("Первый", _received[0].UserName);
            await client.StopAsync();
        }

        [Fact]
        public async Task GarbageMessages_AreSkipped()
        {
            var client = await ConnectedClient();

            Socket(0).Push("не json");
            Socket(0).Push("""{ "metadata": { "message_type": "что-то новое" } }""");
            Socket(0).Push(EventSubMessagesTests.Notification("r1"));
            await WaitFor(() => _received.Count == 1, "заказ после мусора");

            Assert.Equal(EventSubState.Connected, client.State);
            await client.StopAsync();
        }

        [Fact]
        public async Task FailingHandler_DoesNotBreakTheConnection()
        {
            int calls = 0;
            _onRedemption = e => { calls++; throw new InvalidOperationException("сбой обработчика"); };
            var client = await ConnectedClient();

            Socket(0).Push(EventSubMessagesTests.Notification("r1"));
            Socket(0).Push(EventSubMessagesTests.Notification("r2"));
            await WaitFor(() => calls == 2, "оба заказа дошли до обработчика");

            Assert.Equal(EventSubState.Connected, client.State);
            Assert.Equal(1, SocketCount());
            await client.StopAsync();
        }

        [Fact]
        public async Task NoWelcome_TimesOutAndConnectsAgain()
        {
            var client = NewClient();
            client.Start();

            await WaitFor(() => SocketCount() >= 2, "повторное подключение после молчания");

            Assert.Empty(_subscribedSessions);
            Assert.Equal(EventSubState.Connecting, client.State);
            await client.StopAsync();
        }

        [Fact]
        public async Task UnauthorizedSubscription_ReportsAndRetriesLater()
        {
            _subscribeResult = SubscribeResult.Unauthorized;
            var client = NewClient();
            client.Start();
            await WaitFor(() => SocketCount() == 1, "подключение");
            Socket(0).Push(EventSubMessagesTests.Welcome);

            await WaitFor(() => _unauthorizedCalls == 1, "сообщение о недействительном входе");
            await WaitFor(() => SocketCount() >= 2, "повторное подключение");

            Assert.NotEqual(EventSubState.Connected, client.State);
            await client.StopAsync();
        }

        [Fact]
        public async Task FailedSubscription_DoesNotCountAsConnected()
        {
            _subscribeResult = SubscribeResult.Failed;
            var client = NewClient();
            client.Start();
            await WaitFor(() => SocketCount() == 1, "подключение");
            Socket(0).Push(EventSubMessagesTests.Welcome);

            await WaitFor(() => SocketCount() >= 2, "повторное подключение");

            Assert.Equal(0, _unauthorizedCalls);
            Assert.NotEqual(EventSubState.Connected, client.State);
            await client.StopAsync();
        }

        [Fact]
        public async Task ServerClosesConnection_ReconnectsAndSubscribesAgain()
        {
            var client = await ConnectedClient();

            Socket(0).ServerCloses();
            await WaitFor(() => SocketCount() == 2, "новое подключение");
            Socket(1).Push(EventSubMessagesTests.Welcome.Replace("AQoQILE98gtqShGmLD7AM6yJThAB", "second-session"));
            await WaitFor(() => client.State == EventSubState.Connected, "снова Connected");

            Assert.Equal(new[] { "AQoQILE98gtqShGmLD7AM6yJThAB", "second-session" }, _subscribedSessions);
            Assert.True(Socket(0).Disposed);
            Socket(1).Push(EventSubMessagesTests.Notification("after"));
            await WaitFor(() => _received.Count == 1, "заказ на новом соединении");
            await client.StopAsync();
        }

        [Fact]
        public async Task KeepaliveSilence_ReconnectsWithNewSubscription()
        {
            // keepalive_timeout_seconds = 1: тишина дольше секунды с запасом считается потерей связи
            var client = NewClient();
            client.Start();
            await WaitFor(() => SocketCount() == 1, "подключение");
            Socket(0).Push(EventSubMessagesTests.Welcome.Replace("\"keepalive_timeout_seconds\": 10", "\"keepalive_timeout_seconds\": 1"));
            await WaitFor(() => client.State == EventSubState.Connected, "Connected");

            await WaitFor(() => SocketCount() == 2, "переподключение после тишины", ms: 5000);

            Assert.True(Socket(0).Closed);
            await client.StopAsync();
        }

        [Fact]
        public async Task KeepaliveMessages_KeepTheConnectionAlive()
        {
            var client = NewClient();
            client.Start();
            await WaitFor(() => SocketCount() == 1, "подключение");
            Socket(0).Push(EventSubMessagesTests.Welcome.Replace("\"keepalive_timeout_seconds\": 10", "\"keepalive_timeout_seconds\": 1"));
            await WaitFor(() => client.State == EventSubState.Connected, "Connected");

            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(500);
                Socket(0).Push(EventSubMessagesTests.Keepalive);
            }

            Assert.Equal(1, SocketCount());
            Assert.Equal(EventSubState.Connected, client.State);
            await client.StopAsync();
        }

        [Fact]
        public async Task SessionReconnect_MovesToNewConnectionWithoutNewSubscription()
        {
            var client = await ConnectedClient();
            const string url = "wss://eventsub.wss.twitch.tv/ws?challenge=abc";

            Socket(0).Push(EventSubMessagesTests.Reconnect(url));
            await WaitFor(() => SocketCount() == 2, "новое соединение по адресу из сообщения");
            Assert.Equal(url, _connects[1].ToString());
            Socket(1).Push(EventSubMessagesTests.Welcome.Replace("AQoQILE98gtqShGmLD7AM6yJThAB", "moved"));
            await WaitFor(() => Socket(0).Closed, "закрытие старого соединения");

            // подписка осталась с прежним соединением: заново её создавать не нужно
            Assert.Single(_subscribedSessions);
            Assert.Equal(EventSubState.Connected, client.State);

            Socket(1).Push(EventSubMessagesTests.Notification("on-new"));
            await WaitFor(() => _received.Count == 1, "заказ на новом соединении");
            await client.StopAsync();
        }

        [Fact]
        public async Task SessionReconnect_ToForeignAddressIsRefused()
        {
            var client = await ConnectedClient();

            Socket(0).Push(EventSubMessagesTests.Reconnect("wss://evil.example/ws"));

            // вместо перехода по чужому адресу сессия начинается заново по обычному адресу
            await WaitFor(() => SocketCount() == 2, "новое подключение");
            Assert.Equal(EventSubMessages.Endpoint, _connects[1].ToString());
            await client.StopAsync();
        }

        [Fact]
        public async Task Revocation_StartsANewSession()
        {
            var client = await ConnectedClient();

            Socket(0).Push("""{ "metadata": { "message_id": "x", "message_type": "revocation" }, "payload": { "subscription": { "status": "authorization_revoked", "type": "channel.channel_points_custom_reward_redemption.add" } } }""");

            await WaitFor(() => SocketCount() == 2, "новое подключение после отзыва");
            await client.StopAsync();
        }

        [Fact]
        public async Task Backoff_GrowsWhileFailingAndResetsAfterSuccess()
        {
            var client = NewClient();
            client.Start();

            // три неудачи подряд (молчание вместо приветствия)
            await WaitFor(() => { lock (_delays) return _delays.Count >= 3; }, "три паузы");
            List<TimeSpan> grown;
            lock (_delays) grown = _delays.Take(3).ToList();
            Assert.Equal(new[] { 2.0, 4.0, 8.0 }, grown.Select(d => d.TotalSeconds).ToArray());

            // дальше пауза не растёт выше предела
            await WaitFor(() => { lock (_delays) return _delays.Count >= 4; }, "четвёртая пауза");
            lock (_delays) Assert.Equal(8.0, _delays[3].TotalSeconds);

            // удачное подключение возвращает начальную паузу
            int before = SocketCount();
            await WaitFor(() => SocketCount() > before, "новое подключение");
            Socket(SocketCount() - 1).Push(EventSubMessagesTests.Welcome);
            await WaitFor(() => client.State == EventSubState.Connected, "Connected");
            Socket(SocketCount() - 1).ServerCloses();
            await WaitFor(() => { lock (_delays) return _delays.Count > 4 && _delays.Skip(4).Any(d => d.TotalSeconds == 1.0); }, "пауза после успеха");
            await client.StopAsync();
        }

        [Fact]
        public async Task Stop_ClosesConnectionAndStartWorksAgain()
        {
            var client = await ConnectedClient();

            await client.StopAsync();

            Assert.Equal(EventSubState.Stopped, client.State);
            Assert.True(Socket(0).Closed);
            int sockets = SocketCount();

            client.Start();
            await WaitFor(() => SocketCount() == sockets + 1, "подключение после повторного запуска");
            Socket(sockets).Push(EventSubMessagesTests.Welcome);
            await WaitFor(() => client.State == EventSubState.Connected, "Connected после повторного запуска");
            await client.StopAsync();
        }

        [Fact]
        public async Task Start_WhileRunning_DoesNotOpenSecondConnection()
        {
            var client = await ConnectedClient();

            client.Start();
            client.Start();
            await Task.Delay(100);

            Assert.Equal(1, SocketCount());
            await client.StopAsync();
        }

        [Fact]
        public async Task ConnectFailure_IsRetried()
        {
            int attempts = 0;
            var client = new EventSubClient(
                connect: (uri, ct) =>
                {
                    if (++attempts == 1) throw new HttpRequestException("нет сети");
                    var socket = new FakeSocket();
                    socket.Push(EventSubMessagesTests.Welcome);
                    lock (_sockets) _sockets.Add(socket);
                    return Task.FromResult<IEventSubSocket>(socket);
                },
                subscribe: (session, ct) => Task.FromResult(SubscribeResult.Success),
                onRedemption: _ => { },
                timings: Fast,
                delay: async (span, ct) => await Task.Delay(5, ct));
            client.Start();

            await WaitFor(() => client.State == EventSubState.Connected, "Connected после неудачного подключения");

            Assert.Equal(2, attempts);
            await client.StopAsync();
        }
    }
}

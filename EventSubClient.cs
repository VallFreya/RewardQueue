using System.Net.WebSockets;
using System.Text;

namespace RewardQueue
{
    internal enum EventSubState { Stopped, Connecting, Connected }

    internal enum SubscribeResult { Success, Unauthorized, Failed }

    /// <summary>Соединение WebSocket с EventSub: читает сообщения целиком. Подменяется в проверках</summary>
    internal interface IEventSubSocket : IDisposable
    {
        /// <summary>Следующее текстовое сообщение целиком; null, если сервер закрыл соединение</summary>
        Task<string?> ReceiveAsync(CancellationToken ct);

        Task CloseAsync();
    }

    /// <summary>Сроки клиента. Значения по умолчанию взяты из правил Twitch; в проверках сокращаются</summary>
    internal sealed record EventSubTimings(
        TimeSpan WelcomeTimeout,
        TimeSpan SubscribeTimeout,
        TimeSpan KeepaliveMargin,
        TimeSpan InitialBackoff,
        TimeSpan MaxBackoff)
    {
        public static readonly EventSubTimings Default = new EventSubTimings(
            // после подключения Twitch ждёт подписку 10 секунд, иначе закрывает соединение (код 4003)
            WelcomeTimeout: TimeSpan.FromSeconds(10),
            SubscribeTimeout: TimeSpan.FromSeconds(8),
            KeepaliveMargin: TimeSpan.FromSeconds(3),
            InitialBackoff: TimeSpan.FromSeconds(1),
            MaxBackoff: TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Держит соединение с EventSub и передаёт заказы наград. После приветствия создаёт подписку (за отведённые 10 секунд),
    /// следит за keepalive, по session_reconnect переходит на новое соединение (подписка при этом сохраняется),
    /// при обрыве подключается заново с нарастающей паузой и снова подписывается. События, пропущенные во время обрыва,
    /// Twitch не повторяет. Сеть и паузы подменяются в проверках.
    /// </summary>
    internal sealed class EventSubClient : IDisposable
    {
        private readonly Func<Uri, CancellationToken, Task<IEventSubSocket>> _connect;
        private readonly Func<string, CancellationToken, Task<SubscribeResult>> _subscribe;
        private readonly Action<RedemptionEvent> _onRedemption;
        private readonly Func<Task>? _onUnauthorized;
        private readonly EventSubTimings _timings;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly object _lock = new object();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private EventSubState _state = EventSubState.Stopped;
        private string _detail = "";
        private bool _reachedConnected;

        /// <summary>Действующий клиент (создаётся при загрузке плагина)</summary>
        public static EventSubClient? Current { get; set; }

        /// <summary>Состояние изменилось. Вызывается из потока клиента вне блокировок</summary>
        public event Action? Changed;

        public EventSubClient(
            Func<Uri, CancellationToken, Task<IEventSubSocket>> connect,
            Func<string, CancellationToken, Task<SubscribeResult>> subscribe,
            Action<RedemptionEvent> onRedemption,
            Func<Task>? onUnauthorized = null,
            EventSubTimings? timings = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _connect = connect;
            _subscribe = subscribe;
            _onRedemption = onRedemption;
            _onUnauthorized = onUnauthorized;
            _timings = timings ?? EventSubTimings.Default;
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
        }

        public EventSubState State
        {
            get { lock (_lock) return _state; }
        }

        /// <summary>Пояснение к состоянию «не подключено» (причина последней неудачи) или пусто</summary>
        public string Detail
        {
            get { lock (_lock) return _detail; }
        }

        /// <summary>Запускает подключение. Повторный вызов при работающем клиенте ничего не делает</summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_loop != null && !_loop.IsCompleted) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _loop = Task.Run(() => RunAsync(token));
            }
        }

        /// <summary>Останавливает клиент и закрывает соединение</summary>
        public async Task StopAsync()
        {
            Task? loop;
            lock (_lock)
            {
                _cts?.Cancel();
                loop = _loop;
            }

            if (loop != null)
            {
                try
                {
                    await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch
                {
                    // остановка не должна падать: цикл сам записывает свои ошибки
                }
            }

            SetState(EventSubState.Stopped, "");
        }

        public void Dispose()
        {
            lock (_lock) _cts?.Cancel();
        }

        private async Task RunAsync(CancellationToken ct)
        {
            TimeSpan backoff = _timings.InitialBackoff;
            while (!ct.IsCancellationRequested)
            {
                _reachedConnected = false;
                try
                {
                    await RunSessionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Plugin.LogWarn($"EventSub connection lost: {ex.Message}");
                    SetState(EventSubState.Stopped, ex.Message);
                }

                // если соединение успело заработать, пауза начинается заново, иначе нарастает
                backoff = _reachedConnected ? _timings.InitialBackoff : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _timings.MaxBackoff.Ticks));
                try
                {
                    await _delay(backoff, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            SetState(EventSubState.Stopped, "");
        }

        // Одна сессия: подключение, подписка, чтение сообщений. Заканчивается исключением (обрыв, таймаут) или отменой
        private async Task RunSessionAsync(CancellationToken ct)
        {
            SetState(EventSubState.Connecting, "");

            IEventSubSocket socket = await _connect(new Uri(EventSubMessages.Endpoint), ct).ConfigureAwait(false);
            try
            {
                var welcome = await ExpectWelcomeAsync(socket, ct).ConfigureAwait(false);

                using (var subscribeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    subscribeCts.CancelAfter(_timings.SubscribeTimeout);
                    SubscribeResult result;
                    try
                    {
                        result = await _subscribe(welcome.SessionId, subscribeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("subscription request timed out");
                    }

                    if (result != SubscribeResult.Success)
                    {
                        if (result == SubscribeResult.Unauthorized && _onUnauthorized != null)
                        {
                            try { await _onUnauthorized().ConfigureAwait(false); } catch (Exception ex) { Plugin.LogWarn($"Twitch login check failed: {ex.Message}"); }
                        }
                        throw new InvalidOperationException(result == SubscribeResult.Unauthorized ? "subscription was rejected: login is not valid" : "subscription was not created");
                    }
                }

                _reachedConnected = true;
                SetState(EventSubState.Connected, "");
                Plugin.LogInfo("EventSub connected, subscribed to reward redemptions");

                int keepalive = welcome.KeepaliveSeconds;
                while (true)
                {
                    var message = await ReceiveAsync(socket, KeepaliveTimeout(keepalive), ct).ConfigureAwait(false);
                    switch (message.Kind)
                    {
                        case EsMessageKind.Notification:
                            Deliver(message.Redemption!);
                            break;

                        case EsMessageKind.Reconnect:
                            if (!EventSubMessages.IsTrustedReconnectUrl(message.ReconnectUrl))
                            {
                                throw new InvalidOperationException("the reconnect address is not a Twitch address");
                            }

                            // новое соединение открывается и подтверждается до того, как закрывается старое: событий не теряем
                            var next = await _connect(new Uri(message.ReconnectUrl), ct).ConfigureAwait(false);
                            try
                            {
                                var again = await ExpectWelcomeAsync(next, ct).ConfigureAwait(false);
                                keepalive = again.KeepaliveSeconds;
                            }
                            catch
                            {
                                next.Dispose();
                                throw;
                            }

                            var old = socket;
                            socket = next;
                            await CloseQuietlyAsync(old).ConfigureAwait(false);
                            Plugin.LogInfo("EventSub moved to a new connection at the server's request");
                            break;

                        case EsMessageKind.Revocation:
                            throw new InvalidOperationException($"the subscription was revoked ({message.Status})");
                    }
                }
            }
            finally
            {
                await CloseQuietlyAsync(socket).ConfigureAwait(false);
            }
        }

        private TimeSpan KeepaliveTimeout(int keepaliveSeconds) =>
            TimeSpan.FromSeconds(keepaliveSeconds > 0 ? keepaliveSeconds : 10) + _timings.KeepaliveMargin;

        private async Task<EsMessage> ExpectWelcomeAsync(IEventSubSocket socket, CancellationToken ct)
        {
            // сообщения до приветствия не ожидаются, но если придёт что-то лишнее, пропускаем его в пределах общего срока
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timings.WelcomeTimeout);
            try
            {
                while (true)
                {
                    var message = await ReceiveAsync(socket, Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
                    if (message.Kind == EsMessageKind.Welcome) return message;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("no welcome message from Twitch");
            }
        }

        // Следующее понятное сообщение. Закрытие соединения и тишина дольше срока это ошибка: сессию надо начинать заново
        private static async Task<EsMessage> ReceiveAsync(IEventSubSocket socket, TimeSpan timeout, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(timeout);

            while (true)
            {
                string? text;
                try
                {
                    text = await socket.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && cts.IsCancellationRequested && timeout != Timeout.InfiniteTimeSpan)
                {
                    throw new TimeoutException("no messages from Twitch within the keepalive time");
                }

                if (text == null) throw new IOException("the server closed the connection");

                var message = EventSubMessages.Parse(text);
                if (message.Kind == EsMessageKind.Unknown) continue;

                // любое сообщение показывает, что связь жива: срок keepalive отсчитывается заново
                if (timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(timeout);
                return message;
            }
        }

        private void Deliver(RedemptionEvent redemption)
        {
            try
            {
                _onRedemption(redemption);
            }
            catch (Exception ex)
            {
                // сбой в обработке одного заказа не должен рвать соединение
                Plugin.LogError($"Handling a redemption failed: {ex.Message}");
            }
        }

        private static async Task CloseQuietlyAsync(IEventSubSocket socket)
        {
            try
            {
                await socket.CloseAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // соединение уже потеряно или закрывается
            }
            finally
            {
                try { socket.Dispose(); } catch { }
            }
        }

        private void SetState(EventSubState state, string detail)
        {
            lock (_lock)
            {
                if (_state == state && _detail == detail) return;
                _state = state;
                _detail = detail;
            }

            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.LogError($"EventSub change handler failed: {ex.Message}");
            }
        }
    }

    /// <summary>Настоящее соединение: ClientWebSocket с чтением сообщений целиком</summary>
    internal sealed class WebSocketEventSubSocket : IEventSubSocket
    {
        private const int MaxMessageBytes = 1_000_000;

        private readonly ClientWebSocket _socket;

        private WebSocketEventSubSocket(ClientWebSocket socket) => _socket = socket;

        public static async Task<IEventSubSocket> ConnectAsync(Uri uri, CancellationToken ct)
        {
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
                return new WebSocketEventSubSocket(socket);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) throw new IOException("the message is too long");
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }

        public async Task CloseAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            }
        }

        public void Dispose() => _socket.Dispose();
    }
}

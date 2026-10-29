using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Timer = System.Threading.Timer;

namespace RewardQueue
{
    /// <summary>
    /// Небольшой HTTP-сервер на 127.0.0.1: отдаёт страницы /panel и /overlay (встроены в DLL), поток обновлений /events (SSE)
    /// и принимает команды страниц POST-ом. Доступ только с ключом и своим Host (см. ServerGuard).
    /// Сейчас страницы тестовые: проверяют, что цепочка сервер → док/источник OBS работает.
    /// </summary>
    internal sealed class LocalServer : IDisposable
    {
        private const int KeepAliveMs = 15_000;

        private readonly int _port;
        private readonly string _key;
        private readonly HttpListener _listener = new HttpListener();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, Channel<string>> _clients = new ConcurrentDictionary<int, Channel<string>>();
        private int _nextClientId;
        private int _counter;
        private Timer? _ticker;

        public LocalServer(int port, string key)
        {
            _port = port;
            _key = key;
        }

        public static LocalServer? Current { get; private set; }

        /// <summary>Сообщение об ошибке запуска (порт занят и т. п.) или null, если сервер работает</summary>
        public static string? StartError { get; private set; }

        public int Port => _port;
        public int ClientCount => _clients.Count;

        /// <summary>Запускает сервер. При неудаче причина остаётся в StartError, плагин при этом продолжает работать</summary>
        public static void StartCurrent(int port, string key)
        {
            StopCurrent();
            var server = new LocalServer(port, key);
            try
            {
                server.Start();
                Current = server;
                StartError = null;
                Plugin.LogInfo($"Local server started on {ServerGuard.LoopbackHost}:{port}");
            }
            catch (Exception ex)
            {
                server.Dispose();
                StartError = ex is HttpListenerException { ErrorCode: 183 or 32 } ? "port is busy" : ex.Message;
                Plugin.LogError($"Local server failed to start on port {port}: {ex.Message}");
            }
        }

        public static void StopCurrent()
        {
            var server = Current;
            Current = null;
            if (server == null) return;

            server.Dispose();
            Plugin.LogInfo("Local server stopped");
        }

        private void Start()
        {
            _listener.Prefixes.Add($"http://{ServerGuard.LoopbackHost}:{_port}/");
            _listener.Start();

            _ = Task.Run(AcceptLoop);
            _ticker = new Timer(OnTick, null, 1000, 1000);
        }

        public void Dispose()
        {
            try { _ticker?.Dispose(); } catch { }
            try { _cts.Cancel(); } catch { }
            foreach (var client in _clients.Values) client.Writer.TryComplete();
            try { _listener.Close(); } catch { }
        }

        // Тестовое обновление раз в секунду, только если есть кому его отдавать
        private void OnTick(object? state)
        {
            try
            {
                if (_clients.IsEmpty) return;
                Broadcast($"{{\"type\":\"tick\",\"time\":\"{DateTime.Now:HH:mm:ss}\"}}");
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Local server tick failed: {ex.Message}");
            }
        }

        private void Broadcast(string json)
        {
            foreach (var client in _clients.Values) client.Writer.TryWrite(json);
        }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"Local server accept failed: {ex.Message}");
                    await Task.Delay(200).ConfigureAwait(false);
                    continue;
                }

                _ = Task.Run(() => HandleSafe(context));
            }
        }

        private async Task HandleSafe(HttpListenerContext context)
        {
            try
            {
                await Handle(context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // обрыв соединения клиентом это обычное дело; остальное в журнал
                if (!_cts.IsCancellationRequested && ex is not (HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException))
                {
                    Plugin.LogError($"Local server request failed: {ex.Message}");
                }
                try { context.Response.Abort(); } catch { }
            }
        }

        private async Task Handle(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            bool isGet = request.HttpMethod == "GET";

            // Чужим запросам не объясняем, что именно не так
            bool allowed = ServerGuard.HostAllowed(request.Headers["Host"], _port)
                && ServerGuard.KeyMatches(_key, ServerGuard.KeyFromQuery(request.Url?.Query))
                && (isGet || ServerGuard.OriginAllowed(request.Headers["Origin"], _port));
            if (!allowed)
            {
                response.StatusCode = 403;
                response.Close();
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";

            if (isGet && path == "/panel") await SendResource(response, "web/panel.html").ConfigureAwait(false);
            else if (isGet && path == "/overlay") await SendResource(response, "web/overlay.html").ConfigureAwait(false);
            else if (isGet && path == "/events") await ServeEvents(response).ConfigureAwait(false);
            else if (request.HttpMethod == "POST" && path == "/api/bump") Bump(response);
            else
            {
                response.StatusCode = 404;
                response.Close();
            }
        }

        private static async Task SendResource(HttpListenerResponse response, string name)
        {
            using Stream? stream = typeof(LocalServer).Assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                response.StatusCode = 404;
                response.Close();
                return;
            }

            response.ContentType = "text/html; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory).ConfigureAwait(false);
            response.ContentLength64 = memory.Length;
            await response.OutputStream.WriteAsync(memory.GetBuffer().AsMemory(0, (int)memory.Length)).ConfigureAwait(false);
            response.Close();
        }

        private void Bump(HttpListenerResponse response)
        {
            int value = Interlocked.Increment(ref _counter);
            Broadcast($"{{\"type\":\"counter\",\"value\":{value}}}");

            response.StatusCode = 204;
            response.Close();
        }

        // Поток событий для страницы (Server-Sent Events): соединение остаётся открытым, пока страница жива
        private async Task ServeEvents(HttpListenerResponse response)
        {
            response.ContentType = "text/event-stream";
            response.Headers["Cache-Control"] = "no-store";
            response.SendChunked = true;

            var channel = Channel.CreateUnbounded<string>();
            int id = Interlocked.Increment(ref _nextClientId);
            _clients[id] = channel;
            try
            {
                var output = response.OutputStream;
                await WriteEvent(output, $"{{\"type\":\"counter\",\"value\":{Volatile.Read(ref _counter)}}}").ConfigureAwait(false);

                while (!_cts.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    wait.CancelAfter(KeepAliveMs);
                    try
                    {
                        if (!await channel.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false)) break;
                        while (channel.Reader.TryRead(out string? json)) await WriteEvent(output, json).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                    {
                        // тишина: комментарий держит соединение живым и быстро выдаёт ушедшую страницу (запись упадёт)
                        await WriteRaw(output, ": ping\n\n").ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _clients.TryRemove(id, out _);
                channel.Writer.TryComplete();
                try { response.Close(); } catch { }
            }
        }

        private static Task WriteEvent(Stream output, string json) => WriteRaw(output, $"data: {json}\n\n");

        private static async Task WriteRaw(Stream output, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await output.WriteAsync(bytes).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
    }
}

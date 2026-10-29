using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace RewardQueue
{
    /// <summary>
    /// Небольшой HTTP-сервер на 127.0.0.1: отдаёт страницы /panel (док) и /overlay (источник для трансляции), встроенные в DLL,
    /// поток обновлений очереди /events (SSE) и принимает команды дока POST-ом (выполнено, убрать, добавить вручную).
    /// Доступ только с ключом и своим Host, команды ещё и со своим Origin (см. ServerGuard).
    /// </summary>
    internal sealed class LocalServer : IDisposable
    {
        private const int KeepAliveMs = 15_000;
        private const int MaxBodyBytes = 8 * 1024;

        private readonly int _port;
        private readonly string _key;
        private readonly IQueueApi _api;
        private readonly HttpListener _listener = new HttpListener();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ConcurrentDictionary<int, Channel<string>> _clients = new ConcurrentDictionary<int, Channel<string>>();
        private int _nextClientId;

        public LocalServer(int port, string key, IQueueApi api)
        {
            _port = port;
            _key = key;
            _api = api;
        }

        public static LocalServer? Current { get; private set; }

        /// <summary>Сообщение об ошибке запуска (порт занят и т. п.) или null, если сервер работает</summary>
        public static string? StartError { get; private set; }

        public int Port => _port;
        public int ClientCount => _clients.Count;

        /// <summary>Запускает сервер. При неудаче причина остаётся в StartError, плагин при этом продолжает работать</summary>
        public static void StartCurrent(int port, string key, IQueueApi api)
        {
            StopCurrent();
            var server = new LocalServer(port, key, api);
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

        internal void Start()
        {
            _listener.Prefixes.Add($"http://{ServerGuard.LoopbackHost}:{_port}/");
            _listener.Start();

            _ = Task.Run(AcceptLoop);
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            foreach (var client in _clients.Values) client.Writer.TryComplete();
            try { _listener.Close(); } catch { }
        }

        /// <summary>Очередь или список отмеченных наград изменились: все открытые страницы получают новое состояние</summary>
        public void PushState()
        {
            try
            {
                if (_clients.IsEmpty) return;
                string state = _api.BuildState();
                foreach (var client in _clients.Values) client.Writer.TryWrite(state);
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Pushing the queue state failed: {ex.Message}");
            }
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
                Finish(response, 403);
                return;
            }

            string path = request.Url?.AbsolutePath ?? "/";

            if (isGet && path == "/panel") await SendResource(response, "web/panel.html").ConfigureAwait(false);
            else if (isGet && path == "/overlay") await SendResource(response, "web/overlay.html").ConfigureAwait(false);
            else if (isGet && path == "/events") await ServeEvents(response).ConfigureAwait(false);
            else if (request.HttpMethod == "POST" && (path == "/api/done" || path == "/api/remove")) RemoveEntry(request, response);
            else if (request.HttpMethod == "POST" && path == "/api/add") await AddEntry(request, response).ConfigureAwait(false);
            else Finish(response, 404);
        }

        private static void Finish(HttpListenerResponse response, int status)
        {
            response.StatusCode = status;
            response.Close();
        }

        private static async Task SendResource(HttpListenerResponse response, string name)
        {
            using Stream? stream = typeof(LocalServer).Assembly.GetManifestResourceStream(name);
            if (stream == null)
            {
                Finish(response, 404);
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

        // «Выполнено» и «Убрать» для очереди одно и то же: запись удаляется
        private void RemoveEntry(HttpListenerRequest request, HttpListenerResponse response)
        {
            string id = request.QueryString["id"] ?? "";
            Finish(response, id.Length > 0 && _api.Remove(id) ? 204 : 404);
        }

        private async Task AddEntry(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.ContentLength64 > MaxBodyBytes)
            {
                Finish(response, 413);
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8, false, 1024, leaveOpen: true))
            {
                char[] buffer = new char[MaxBodyBytes + 1];
                int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read > MaxBodyBytes)
                {
                    Finish(response, 413);
                    return;
                }
                body = new string(buffer, 0, read);
            }

            var add = QueueApi.ParseAddRequest(body);
            string? id = add == null ? null : _api.AddManual(add.title, add.user, add.input);
            if (id == null)
            {
                Finish(response, 400);
                return;
            }

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new AddResponse { id = id }, WebJsonContext.Default.AddResponse);
            response.StatusCode = 200;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = json.Length;
            await response.OutputStream.WriteAsync(json).ConfigureAwait(false);
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
                // страница сразу получает текущее состояние
                await WriteEvent(output, _api.BuildState()).ConfigureAwait(false);

                while (!_cts.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    wait.CancelAfter(KeepAliveMs);
                    try
                    {
                        if (!await channel.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false)) break;

                        // если накопилось несколько обновлений, нужно только последнее: оно содержит всё состояние
                        string? latest = null;
                        while (channel.Reader.TryRead(out string? state)) latest = state;
                        if (latest != null) await WriteEvent(output, latest).ConfigureAwait(false);
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

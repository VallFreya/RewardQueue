using System.Net.WebSockets;
using System.Text;

namespace RewardQueue
{
    /// <summary>
    /// Пробное подключение к EventSub: открывает WebSocket, ждёт приветствие и закрывает соединение. Входа в Twitch не требует.
    /// Нужно, чтобы убедиться, что ClientWebSocket (TLS, разбор сообщений) работает в NativeAOT внутри OBS.
    /// </summary>
    internal static class EventSubProbe
    {
        private const int TimeoutMs = 10_000;

        /// <summary>Результат последней проверки для окна настроек (null, пока проверки не было)</summary>
        public static volatile string? LastResult;
        public static volatile bool Running;

        public static async Task RunAsync()
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) return;
            Running = true;
            try
            {
                LastResult = await ConnectAndRead().ConfigureAwait(false);
                Plugin.LogInfo($"EventSub probe: {LastResult}");
            }
            catch (Exception ex)
            {
                LastResult = $"error: {ex.Message}";
                Plugin.LogError($"EventSub probe failed: {ex}");
            }
            finally
            {
                Running = false;
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static int _running;

        private static async Task<string> ConnectAndRead()
        {
            using var cts = new CancellationTokenSource(TimeoutMs);
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(EventSubMessages.Endpoint), cts.Token).ConfigureAwait(false);

            string text = await ReceiveText(socket, cts.Token).ConfigureAwait(false);
            string result = EventSubMessages.TryParseWelcome(text, out _, out int keepalive)
                ? $"ok, session_welcome received (keepalive {keepalive} s)"
                : $"unexpected first message: {EventSubMessages.GetMessageType(text) ?? "not json"}";

            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "probe", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // проверка уже удалась, как закрылось соединение не важно
            }

            return result;
        }

        // Сообщение может прийти кусками: собираем до признака конца
        private static async Task<string> ReceiveText(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[8192];
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) throw new IOException("the server closed the connection");
                message.Write(buffer, 0, result.Count);
                if (message.Length > 1_000_000) throw new IOException("the message is too long");
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }
}

using System.Text.Json;

namespace RewardQueue
{
    /// <summary>Разбор сообщений EventSub WebSocket, без сети и OBS</summary>
    internal static class EventSubMessages
    {
        public const string Endpoint = "wss://eventsub.wss.twitch.tv/ws";

        /// <summary>Тип сообщения из metadata.message_type, null если это не JSON нужной формы</summary>
        public static string? GetMessageType(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("metadata").GetProperty("message_type").GetString();
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>Приветствие session_welcome: id сессии (нужен для подписки) и срок молчания, после которого связь считается потерянной</summary>
        public static bool TryParseWelcome(string json, out string sessionId, out int keepaliveSeconds)
        {
            sessionId = "";
            keepaliveSeconds = 0;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.GetProperty("metadata").GetProperty("message_type").GetString() != "session_welcome") return false;

                var session = root.GetProperty("payload").GetProperty("session");
                string? id = session.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(id)) return false;

                sessionId = id;
                keepaliveSeconds = session.TryGetProperty("keepalive_timeout_seconds", out var keep) && keep.ValueKind == JsonValueKind.Number
                    ? keep.GetInt32()
                    : 0;
                return true;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return false;
            }
        }
    }
}

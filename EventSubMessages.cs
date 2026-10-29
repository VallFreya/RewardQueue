using System.Text.Json;

namespace RewardQueue
{
    internal enum EsMessageKind { Unknown, Welcome, Keepalive, Notification, Reconnect, Revocation }

    /// <summary>Заказ награды из события channel.channel_points_custom_reward_redemption.add</summary>
    internal sealed record RedemptionEvent(
        string Id,
        string UserLogin,
        string UserName,
        string UserInput,
        string RewardId,
        string RewardTitle,
        DateTimeOffset RedeemedAt);

    /// <summary>Одно сообщение EventSub WebSocket после разбора. Поля, которых в сообщении этого типа нет, пустые</summary>
    internal sealed record EsMessage(
        EsMessageKind Kind,
        string MessageId = "",
        string SessionId = "",
        int KeepaliveSeconds = 0,
        string ReconnectUrl = "",
        string SubscriptionType = "",
        string Status = "",
        RedemptionEvent? Redemption = null);

    /// <summary>Разбор сообщений EventSub WebSocket, без сети и OBS</summary>
    internal static class EventSubMessages
    {
        public const string Endpoint = "wss://eventsub.wss.twitch.tv/ws";
        public const string RedemptionType = "channel.channel_points_custom_reward_redemption.add";

        /// <summary>
        /// Разбирает сообщение. Неизвестный тип и не-JSON дают Unknown: клиент такие сообщения пропускает.
        /// Уведомление с типом подписки, отличным от заказов наград, тоже Unknown (мы на другое не подписывались)
        /// </summary>
        public static EsMessage Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var metadata = root.GetProperty("metadata");
                string type = metadata.GetProperty("message_type").GetString() ?? "";
                string messageId = Str(metadata, "message_id");
                root.TryGetProperty("payload", out var payload);

                switch (type)
                {
                    case "session_welcome":
                    {
                        var session = payload.GetProperty("session");
                        string id = Str(session, "id");
                        if (id.Length == 0) return new EsMessage(EsMessageKind.Unknown);
                        return new EsMessage(EsMessageKind.Welcome, messageId, SessionId: id,
                            KeepaliveSeconds: Int(session, "keepalive_timeout_seconds"));
                    }

                    case "session_keepalive":
                        return new EsMessage(EsMessageKind.Keepalive, messageId);

                    case "session_reconnect":
                    {
                        var session = payload.GetProperty("session");
                        string url = Str(session, "reconnect_url");
                        return url.Length == 0
                            ? new EsMessage(EsMessageKind.Unknown)
                            : new EsMessage(EsMessageKind.Reconnect, messageId, SessionId: Str(session, "id"), ReconnectUrl: url);
                    }

                    case "revocation":
                    {
                        var subscription = payload.GetProperty("subscription");
                        return new EsMessage(EsMessageKind.Revocation, messageId,
                            SubscriptionType: Str(subscription, "type"), Status: Str(subscription, "status"));
                    }

                    case "notification":
                    {
                        string subscriptionType = Str(metadata, "subscription_type");
                        if (subscriptionType != RedemptionType) return new EsMessage(EsMessageKind.Unknown);

                        var redemption = ParseRedemption(payload.GetProperty("event"));
                        return redemption == null
                            ? new EsMessage(EsMessageKind.Unknown)
                            : new EsMessage(EsMessageKind.Notification, messageId, SubscriptionType: subscriptionType, Redemption: redemption);
                    }

                    default:
                        return new EsMessage(EsMessageKind.Unknown);
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return new EsMessage(EsMessageKind.Unknown);
            }
        }

        /// <summary>Адрес для переподключения должен быть адресом Twitch по wss: ответу сервера не доверяем вслепую</summary>
        public static bool IsTrustedReconnectUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "wss"
            && (uri.Host == "twitch.tv" || uri.Host.EndsWith(".twitch.tv", StringComparison.Ordinal));

        private static RedemptionEvent? ParseRedemption(JsonElement ev)
        {
            string id = Str(ev, "id");
            var reward = ev.GetProperty("reward");
            string rewardId = Str(reward, "id");
            if (id.Length == 0 || rewardId.Length == 0) return null;

            // время заказа из события; если его нет или оно не читается, берём текущее
            DateTimeOffset redeemedAt = DateTimeOffset.TryParse(Str(ev, "redeemed_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UtcNow;

            return new RedemptionEvent(
                Id: id,
                UserLogin: Str(ev, "user_login"),
                UserName: Str(ev, "user_name"),
                UserInput: Str(ev, "user_input"),
                RewardId: rewardId,
                RewardTitle: Str(reward, "title"),
                RedeemedAt: redeemedAt);
        }

        private static string Str(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";

        private static int Int(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;
    }
}

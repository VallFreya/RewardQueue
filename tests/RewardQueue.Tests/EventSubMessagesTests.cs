using Xunit;

namespace RewardQueue.Tests
{
    public class EventSubMessagesTests
    {
        // Сообщения в том виде, как их описывает документация Twitch
        internal const string Welcome = """
            {
              "metadata": { "message_id": "96a3f3b5", "message_type": "session_welcome", "message_timestamp": "2023-07-19T14:56:51.634234626Z" },
              "payload": { "session": { "id": "AQoQILE98gtqShGmLD7AM6yJThAB", "status": "connected", "connected_at": "2023-07-19T14:56:51.616329898Z", "keepalive_timeout_seconds": 10, "reconnect_url": null } }
            }
            """;

        internal const string Keepalive = """{ "metadata": { "message_id": "84c1e79a", "message_type": "session_keepalive" }, "payload": {} }""";

        internal static string Notification(string id = "redemption-1", string rewardId = "reward-1", string title = "Заказ техники",
            string user = "Зритель", string input = "хочу клавиатуру", string at = "2026-10-29T12:30:00.123456789Z") => $$"""
            {
              "metadata": { "message_id": "msg-{{id}}", "message_type": "notification", "message_timestamp": "2026-10-29T12:30:01Z", "subscription_type": "channel.channel_points_custom_reward_redemption.add", "subscription_version": "1" },
              "payload": {
                "subscription": { "id": "sub1", "type": "channel.channel_points_custom_reward_redemption.add", "version": "1", "status": "enabled", "cost": 0 },
                "event": {
                  "id": "{{id}}", "broadcaster_user_id": "1", "broadcaster_user_login": "streamer", "broadcaster_user_name": "Streamer",
                  "user_id": "2", "user_login": "viewer", "user_name": "{{user}}", "user_input": "{{input}}", "status": "unfulfilled",
                  "reward": { "id": "{{rewardId}}", "title": "{{title}}", "cost": 100, "prompt": "описание" },
                  "redeemed_at": "{{at}}"
                }
              }
            }
            """;

        internal static string Reconnect(string url = "wss://eventsub.wss.twitch.tv/ws?challenge=abc") => $$"""
            { "metadata": { "message_id": "r1", "message_type": "session_reconnect" }, "payload": { "session": { "id": "AQoQexAWVYKSTIu4ec_2VAxyuhAB", "status": "reconnecting", "keepalive_timeout_seconds": null, "reconnect_url": "{{url}}" } } }
            """;

        [Fact]
        public void Parse_Welcome_ReadsSessionIdAndKeepalive()
        {
            var message = EventSubMessages.Parse(Welcome);

            Assert.Equal(EsMessageKind.Welcome, message.Kind);
            Assert.Equal("AQoQILE98gtqShGmLD7AM6yJThAB", message.SessionId);
            Assert.Equal(10, message.KeepaliveSeconds);
        }

        [Fact]
        public void Parse_Welcome_KeepaliveIsOptional()
        {
            var message = EventSubMessages.Parse("""{ "metadata": { "message_type": "session_welcome" }, "payload": { "session": { "id": "abc" } } }""");

            Assert.Equal(EsMessageKind.Welcome, message.Kind);
            Assert.Equal(0, message.KeepaliveSeconds);
        }

        [Fact]
        public void Parse_Keepalive()
        {
            Assert.Equal(EsMessageKind.Keepalive, EventSubMessages.Parse(Keepalive).Kind);
        }

        [Fact]
        public void Parse_Notification_ReadsAllRedemptionFields()
        {
            var message = EventSubMessages.Parse(Notification());

            Assert.Equal(EsMessageKind.Notification, message.Kind);
            var r = message.Redemption!;
            Assert.Equal("redemption-1", r.Id);
            Assert.Equal("viewer", r.UserLogin);
            Assert.Equal("Зритель", r.UserName);
            Assert.Equal("хочу клавиатуру", r.UserInput);
            Assert.Equal("reward-1", r.RewardId);
            Assert.Equal("Заказ техники", r.RewardTitle);
            // доли секунды приходят с девятью знаками, точность до микросекунды достаточна
            var expected = new DateTimeOffset(2026, 10, 29, 12, 30, 0, 123, TimeSpan.Zero);
            Assert.InRange((r.RedeemedAt - expected).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        }

        [Fact]
        public void Parse_Notification_WithoutUserInputGivesEmptyText()
        {
            string json = Notification().Replace("\"user_input\": \"хочу клавиатуру\",", "");

            Assert.Equal("", EventSubMessages.Parse(json).Redemption!.UserInput);
        }

        [Fact]
        public void Parse_Notification_BadTimeFallsBackToNow()
        {
            var before = DateTimeOffset.UtcNow.AddSeconds(-1);

            var redemption = EventSubMessages.Parse(Notification(at: "вчера")).Redemption!;

            Assert.True(redemption.RedeemedAt >= before);
        }

        [Fact]
        public void Parse_Notification_OfAnotherSubscriptionTypeIsIgnored()
        {
            string json = Notification().Replace("\"subscription_type\": \"channel.channel_points_custom_reward_redemption.add\"", "\"subscription_type\": \"channel.follow\"");

            Assert.Equal(EsMessageKind.Unknown, EventSubMessages.Parse(json).Kind);
        }

        [Fact]
        public void Parse_Notification_WithoutIdsIsIgnored()
        {
            Assert.Equal(EsMessageKind.Unknown, EventSubMessages.Parse(Notification(id: "")).Kind);
            Assert.Equal(EsMessageKind.Unknown, EventSubMessages.Parse(Notification(rewardId: "")).Kind);
        }

        [Fact]
        public void Parse_Reconnect_ReadsUrl()
        {
            var message = EventSubMessages.Parse(Reconnect());

            Assert.Equal(EsMessageKind.Reconnect, message.Kind);
            Assert.Equal("wss://eventsub.wss.twitch.tv/ws?challenge=abc", message.ReconnectUrl);
        }

        [Fact]
        public void Parse_Revocation_ReadsReason()
        {
            var message = EventSubMessages.Parse("""
                { "metadata": { "message_id": "x", "message_type": "revocation", "subscription_type": "channel.follow" },
                  "payload": { "subscription": { "id": "s", "status": "authorization_revoked", "type": "channel.channel_points_custom_reward_redemption.add" } } }
                """);

            Assert.Equal(EsMessageKind.Revocation, message.Kind);
            Assert.Equal("authorization_revoked", message.Status);
        }

        [Theory]
        [InlineData("")]
        [InlineData("не json")]
        [InlineData("{}")]
        [InlineData("""{ "metadata": { "message_type": "что-то новое" } }""")]
        [InlineData("""{ "metadata": { "message_type": "session_welcome" }, "payload": { "session": { "id": "" } } }""")]
        [InlineData("""{ "metadata": { "message_type": "session_welcome" }, "payload": {} }""")]
        [InlineData("""{ "metadata": { "message_type": "session_reconnect" }, "payload": { "session": {} } }""")]
        public void Parse_GarbageGivesUnknown(string json)
        {
            Assert.Equal(EsMessageKind.Unknown, EventSubMessages.Parse(json).Kind);
        }

        [Theory]
        [InlineData("wss://eventsub.wss.twitch.tv/ws?challenge=abc", true)]
        [InlineData("wss://twitch.tv/ws", true)]
        [InlineData("ws://eventsub.wss.twitch.tv/ws", false)]
        [InlineData("wss://evil.example/ws", false)]
        [InlineData("wss://nottwitch.tv/ws", false)]
        [InlineData("", false)]
        public void IsTrustedReconnectUrl_OnlyTwitchOverWss(string url, bool expected)
        {
            Assert.Equal(expected, EventSubMessages.IsTrustedReconnectUrl(url));
        }
    }
}

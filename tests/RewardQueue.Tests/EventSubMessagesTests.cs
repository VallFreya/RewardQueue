using Xunit;

namespace RewardQueue.Tests
{
    public class EventSubMessagesTests
    {
        // Приветствие в том виде, как его описывает документация Twitch
        private const string Welcome = """
            {
              "metadata": { "message_id": "96a3f3b5", "message_type": "session_welcome", "message_timestamp": "2023-07-19T14:56:51.634234626Z" },
              "payload": { "session": { "id": "AQoQILE98gtqShGmLD7AM6yJThAB", "status": "connected", "connected_at": "2023-07-19T14:56:51.616329898Z", "keepalive_timeout_seconds": 10, "reconnect_url": null } }
            }
            """;

        [Fact]
        public void TryParseWelcome_ReadsSessionIdAndKeepalive()
        {
            Assert.True(EventSubMessages.TryParseWelcome(Welcome, out string id, out int keepalive));
            Assert.Equal("AQoQILE98gtqShGmLD7AM6yJThAB", id);
            Assert.Equal(10, keepalive);
        }

        [Fact]
        public void TryParseWelcome_RejectsOtherMessageTypes()
        {
            string keepaliveMessage = """{ "metadata": { "message_type": "session_keepalive" }, "payload": {} }""";
            Assert.False(EventSubMessages.TryParseWelcome(keepaliveMessage, out _, out _));
        }

        [Theory]
        [InlineData("")]
        [InlineData("не json")]
        [InlineData("{}")]
        [InlineData("""{ "metadata": { "message_type": "session_welcome" }, "payload": {} }""")]
        [InlineData("""{ "metadata": { "message_type": "session_welcome" }, "payload": { "session": { "id": "" } } }""")]
        public void TryParseWelcome_RejectsBrokenMessages(string json)
        {
            Assert.False(EventSubMessages.TryParseWelcome(json, out _, out _));
        }

        [Fact]
        public void TryParseWelcome_KeepaliveIsOptional()
        {
            string json = """{ "metadata": { "message_type": "session_welcome" }, "payload": { "session": { "id": "abc" } } }""";
            Assert.True(EventSubMessages.TryParseWelcome(json, out string id, out int keepalive));
            Assert.Equal("abc", id);
            Assert.Equal(0, keepalive);
        }

        [Fact]
        public void GetMessageType_ReturnsTypeOrNull()
        {
            Assert.Equal("session_welcome", EventSubMessages.GetMessageType(Welcome));
            Assert.Null(EventSubMessages.GetMessageType("{}"));
            Assert.Null(EventSubMessages.GetMessageType("мусор"));
        }
    }
}

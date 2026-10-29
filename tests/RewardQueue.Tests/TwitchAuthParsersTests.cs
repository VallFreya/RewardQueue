using Xunit;

namespace RewardQueue.Tests
{
    public class TwitchAuthParsersTests
    {
        private const string Device = """{"device_code":"dc123","expires_in":1800,"interval":5,"user_code":"ABCDEFGH","verification_uri":"https://www.twitch.tv/activate?device-code=ABCDEFGH"}""";
        private const string Tokens = """{"access_token":"at1","expires_in":14400,"refresh_token":"rt1","scope":["channel:read:redemptions"],"token_type":"bearer"}""";

        [Fact]
        public void TryParseDeviceCode_ReadsAllFields()
        {
            Assert.True(TwitchAuthParsers.TryParseDeviceCode(Device, out var device));
            Assert.Equal("dc123", device.device_code);
            Assert.Equal("ABCDEFGH", device.user_code);
            Assert.Equal("https://www.twitch.tv/activate?device-code=ABCDEFGH", device.verification_uri);
            Assert.Equal(1800, device.expires_in);
            Assert.Equal(5, device.interval);
        }

        [Theory]
        [InlineData("""{"device_code":"d","user_code":"U","verification_uri":"https://x","expires_in":60,"interval":0}""", 5)]
        [InlineData("""{"device_code":"d","user_code":"U","verification_uri":"https://x","expires_in":60,"interval":999}""", 30)]
        [InlineData("""{"device_code":"d","user_code":"U","verification_uri":"https://x","expires_in":60,"interval":7}""", 7)]
        public void TryParseDeviceCode_KeepsPollIntervalInRange(string body, int expected)
        {
            Assert.True(TwitchAuthParsers.TryParseDeviceCode(body, out var device));
            Assert.Equal(expected, device.interval);
        }

        [Theory]
        [InlineData("")]
        [InlineData("не json")]
        [InlineData("{}")]
        [InlineData("""{"device_code":"","user_code":"U","verification_uri":"https://x","expires_in":60}""")]
        [InlineData("""{"device_code":"d","user_code":"U","verification_uri":"https://x","expires_in":0}""")]
        public void TryParseDeviceCode_RejectsIncompleteAnswers(string body)
        {
            Assert.False(TwitchAuthParsers.TryParseDeviceCode(body, out _));
        }

        [Fact]
        public void ParseTokenPoll_Success()
        {
            var poll = TwitchAuthParsers.ParseTokenPoll(200, Tokens);
            Assert.Equal(TokenPollKind.Success, poll.Kind);
            Assert.Equal("at1", poll.Tokens!.access_token);
            Assert.Equal("rt1", poll.Tokens.refresh_token);
            Assert.Contains("channel:read:redemptions", poll.Tokens.scope!);
        }

        [Theory]
        [InlineData("""{"status":400,"message":"authorization_pending"}""", "Pending")]
        [InlineData("""{"status":400,"message":"slow_down"}""", "SlowDown")]
        [InlineData("""{"status":400,"message":"access_denied"}""", "Denied")]
        [InlineData("""{"status":400,"message":"expired_token"}""", "Expired")]
        [InlineData("""{"status":400,"message":"invalid device code"}""", "Expired")]
        [InlineData("""{"status":500,"message":"что-то сломалось"}""", "Failed")]
        [InlineData("не json", "Failed")]
        public void ParseTokenPoll_RecognizesStates(string body, string expected)
        {
            Assert.Equal(Enum.Parse<TokenPollKind>(expected), TwitchAuthParsers.ParseTokenPoll(400, body).Kind);
        }

        [Fact]
        public void ParseTokenPoll_IncompleteSuccessIsFailure()
        {
            Assert.Equal(TokenPollKind.Failed, TwitchAuthParsers.ParseTokenPoll(200, """{"access_token":"a"}""").Kind);
        }

        [Fact]
        public void ParseRefresh_SuccessAndRejectionAndOutage()
        {
            Assert.Equal(RefreshKind.Success, TwitchAuthParsers.ParseRefresh(200, Tokens).Kind);
            Assert.Equal(RefreshKind.InvalidGrant, TwitchAuthParsers.ParseRefresh(400, """{"status":400,"message":"Invalid refresh token"}""").Kind);
            Assert.Equal(RefreshKind.InvalidGrant, TwitchAuthParsers.ParseRefresh(401, "").Kind);
            Assert.Equal(RefreshKind.Failed, TwitchAuthParsers.ParseRefresh(503, "").Kind);
            Assert.Equal(RefreshKind.Failed, TwitchAuthParsers.ParseRefresh(200, "{}").Kind);
        }

        [Fact]
        public void TryParseValidate_ReadsUserAndScopes()
        {
            const string body = """{"client_id":"c","login":"streamer","scopes":["channel:read:redemptions"],"user_id":"12345","expires_in":5000}""";

            Assert.True(TwitchAuthParsers.TryParseValidate(body, out var validate));
            Assert.Equal("streamer", validate.login);
            Assert.Equal("12345", validate.user_id);
            Assert.Equal(5000, validate.expires_in);
            Assert.False(TwitchAuthParsers.TryParseValidate("""{"status":401,"message":"invalid access token"}""", out _));
        }

        [Fact]
        public void HasRequiredScope_ChecksTheRedemptionsScope()
        {
            Assert.True(TwitchAuthParsers.HasRequiredScope(new[] { "chat:read", "channel:read:redemptions" }));
            Assert.False(TwitchAuthParsers.HasRequiredScope(new[] { "chat:read" }));
            Assert.False(TwitchAuthParsers.HasRequiredScope(null));
        }

        [Theory]
        [InlineData("g1e25xwrf3oqosokb3r98vpqhhj0pd", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("short", false)]
        [InlineData("G1E25XWRF3OQOSOKB3R98VPQHHJ0PD", false)]
        [InlineData("g1e25xwrf3oqosokb3r98vpqhhj0p!", false)]
        public void ValidClientId_AcceptsOnlyTwitchStyleIds(string? id, bool expected)
        {
            Assert.Equal(expected, TwitchAuth.ValidClientId(id));
        }

        [Fact]
        public void DefaultClientId_IsValid()
        {
            Assert.True(TwitchAuth.ValidClientId(TwitchAuth.DefaultClientId));
        }
    }
}

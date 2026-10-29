using Xunit;

namespace RewardQueue.Tests
{
    public class ServerGuardTests
    {
        [Theory]
        [InlineData(0, ServerGuard.DefaultPort)]
        [InlineData(80, ServerGuard.DefaultPort)]
        [InlineData(70000, ServerGuard.DefaultPort)]
        [InlineData(1024, 1024)]
        [InlineData(50000, 50000)]
        [InlineData(65535, 65535)]
        public void ClampPort_FallsBackToDefaultOutsideRange(int port, int expected)
        {
            Assert.Equal(expected, ServerGuard.ClampPort(port));
        }

        [Fact]
        public void NewKey_IsValidAndDifferentEveryTime()
        {
            string a = ServerGuard.NewKey();
            string b = ServerGuard.NewKey();

            Assert.True(ServerGuard.IsValidKey(a));
            Assert.NotEqual(a, b);
            Assert.DoesNotContain('=', a);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("short", false)]
        [InlineData("abcdefghijklmnop", true)]
        [InlineData("abcdefghijklmnop?x=1", false)]
        [InlineData("абвгдеёжзийклмноп", false)]
        public void IsValidKey_RejectsShortOrUnsafe(string? key, bool expected)
        {
            Assert.Equal(expected, ServerGuard.IsValidKey(key));
        }

        [Theory]
        [InlineData("abc", "abc", true)]
        [InlineData("abc", "abd", false)]
        [InlineData("abc", "abcd", false)]
        [InlineData("abc", "", false)]
        [InlineData("abc", null, false)]
        [InlineData("", "", false)]
        [InlineData(null, null, false)]
        public void KeyMatches_ComparesExactly(string? expected, string? given, bool result)
        {
            Assert.Equal(result, ServerGuard.KeyMatches(expected, given));
        }

        [Theory]
        [InlineData("127.0.0.1:47615", true)]
        [InlineData("localhost:47615", true)]
        [InlineData("LOCALHOST:47615", true)]
        [InlineData("evil.example:47615", false)]
        [InlineData("127.0.0.1:47616", false)]
        [InlineData("127.0.0.1", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void HostAllowed_OnlyOwnAddress(string? host, bool expected)
        {
            Assert.Equal(expected, ServerGuard.HostAllowed(host, 47615));
        }

        [Theory]
        [InlineData("http://127.0.0.1:47615", true)]
        [InlineData("http://localhost:47615", true)]
        [InlineData("https://127.0.0.1:47615", false)]
        [InlineData("http://evil.example", false)]
        [InlineData("null", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void OriginAllowed_OnlyOwnPages(string? origin, bool expected)
        {
            Assert.Equal(expected, ServerGuard.OriginAllowed(origin, 47615));
        }

        [Theory]
        [InlineData("?key=abc", "abc")]
        [InlineData("key=abc", "abc")]
        [InlineData("?x=1&key=a-b_c&y=2", "a-b_c")]
        [InlineData("?key=a%20b", "a b")]
        [InlineData("?monkey=abc", null)]
        [InlineData("?key", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void KeyFromQuery_FindsKeyParameter(string? query, string? expected)
        {
            Assert.Equal(expected, ServerGuard.KeyFromQuery(query));
        }

        [Fact]
        public void BuildUrl_PutsPortPageAndKey()
        {
            Assert.Equal("http://127.0.0.1:47615/panel?key=abc", ServerGuard.BuildUrl(47615, "abc", "panel"));
        }
    }
}

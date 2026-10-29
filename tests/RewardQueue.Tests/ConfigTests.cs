using Xunit;

namespace RewardQueue.Tests
{
    public class ConfigTests : IClassFixture<PluginFixture>
    {
        private readonly PluginFixture _fx;

        public ConfigTests(PluginFixture fixture)
        {
            _fx = fixture;
            try { File.Delete(_fx.ConfigPath); } catch { }
            try { File.Delete(_fx.ConfigPath + ".broken"); } catch { }
        }

        [Fact]
        public void TryParseConfig_ReadsTheme()
        {
            Assert.True(Plugin.TryParseConfig("{\"theme\":\"light\"}", out var config, out var error));
            Assert.Null(error);
            Assert.Equal("light", config!.theme);
        }

        [Theory]
        [InlineData("")]
        [InlineData("null")]
        [InlineData("{ обрыв")]
        public void TryParseConfig_RejectsBrokenText(string json)
        {
            Assert.False(Plugin.TryParseConfig(json, out var config, out var error));
            Assert.Null(config);
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void SetTheme_WritesFileThatLoadsBack()
        {
            Plugin.SetTheme(false);
            Assert.True(File.Exists(_fx.ConfigPath));

            Plugin.SetTheme(true);
            Plugin.LoadConfig();
            Assert.True(Plugin.ThemeDark);

            Plugin.SetTheme(false);
            Plugin.LoadConfig();
            Assert.False(Plugin.ThemeDark);
            Plugin.SetTheme(true);
        }

        [Fact]
        public void LoadConfig_KeepsCopyOfUnreadableFile()
        {
            File.WriteAllText(_fx.ConfigPath, "{ не json");
            Plugin.LoadConfig();
            Assert.Equal("{ не json", File.ReadAllText(_fx.ConfigPath + ".broken"));
        }

        [Fact]
        public void ConfigDirectory_IsFolderOfConfigFile()
        {
            Assert.Equal(_fx.Dir, Plugin.GetConfigDirectory());
        }
    }
}

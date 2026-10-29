using Xunit;

namespace RewardQueue.Tests
{
    public class ThemeTests : IClassFixture<PluginFixture>
    {
        private readonly PluginFixture _fx;

        public ThemeTests(PluginFixture fixture)
        {
            _fx = fixture;
            try { File.Delete(_fx.ConfigPath); } catch { }
        }

        // Окна WinForms живут в своём STA-потоке, как в плагине
        private static void RunSta(Action action)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(60000), "окно зависло");
            if (error != null) throw new Exception("Сбой в потоке окна", error);
        }

        [Fact]
        public void NewConfig_IsDarkByDefault()
        {
            Assert.Equal("dark", new PluginConfig().theme);
        }

        [Fact]
        public void SettingsForm_SwitchesThemeAndRemembersIt()
        {
            RunSta(() =>
            {
                Plugin.SetTheme(true);
                using var form = new SettingsForm();
                _ = form.Handle;
                Assert.True(ObsTheme.IsDark);

                form.SwitchTheme(false);
                Assert.False(ObsTheme.IsDark);
                Assert.False(Plugin.ThemeDark);
                Assert.Contains("\"light\"", File.ReadAllText(_fx.ConfigPath));

                // после перезапуска OBS тема читается из конфига
                Plugin.LoadConfig();
                Assert.False(Plugin.ThemeDark);

                form.SwitchTheme(true);
                Assert.True(ObsTheme.IsDark);
                Assert.Contains("\"dark\"", File.ReadAllText(_fx.ConfigPath));
            });
        }
    }
}

using System.Windows.Forms;
using Xunit;

namespace RewardQueue.Tests
{
    // Подпись внизу окна: обычный текст, при изменении «Сохраняю…», затем «Настройки сохранены» и снова обычный текст
    public class SaveHintTests : IClassFixture<PluginFixture>
    {
        private readonly PluginFixture _fx;

        public SaveHintTests(PluginFixture fixture) => _fx = fixture;

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

        private static string HintOf(Form form) =>
            form.Controls.OfType<Panel>().Single(c => c.Dock == DockStyle.Bottom).Controls.OfType<Label>().Single().Text;

        private static void WaitFor(Func<bool> condition, string what, int ms = 5000)
        {
            for (int i = 0; i < ms / 10 && !condition(); i++)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Assert.True(condition(), "не дождались: " + what);
        }

        private SettingsForm OpenForm()
        {
            Loc.LanguageOverride = true;
            Plugin.SetTheme(true);
            var form = new SettingsForm { SavingHintMs = 150, SavedHintMs = 400 };
            _ = form.Handle;
            form.Show();
            return form;
        }

        [Fact]
        public void ThemeChange_ShowsSavingThenSavedThenTheUsualText()
        {
            RunSta(() =>
            {
                try
                {
                    using var form = OpenForm();
                    Assert.Equal(Loc.AutoSaveHint, HintOf(form));

                    form.SwitchTheme(false);
                    Assert.Equal(Loc.SavingHint, HintOf(form));

                    WaitFor(() => HintOf(form) == Loc.SavedHint, "«Настройки сохранены»");
                    WaitFor(() => HintOf(form) == Loc.AutoSaveHint, "возврат обычного текста");
                    Assert.Equal(Loc.AutoSaveHint, HintOf(form));
                }
                finally
                {
                    Loc.LanguageOverride = null;
                    Plugin.SetTheme(true);
                }
            });
        }

        [Fact]
        public void RewardCheckbox_ShowsTheSameSequenceAndSavesTheMark()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    Plugin.UpdateRewards(r => r.WithKnown(new[] { new RewardInfo { id = "a", title = "Заказ техники" } }));
                    using var form = OpenForm();
                    var box = FindCheckBox(form);

                    box.Checked = true;

                    Assert.Equal(Loc.SavingHint, HintOf(form));
                    Assert.True(Plugin.Rewards.IsTracked("a"));
                    WaitFor(() => HintOf(form) == Loc.SavedHint, "«Настройки сохранены»");
                    WaitFor(() => HintOf(form) == Loc.AutoSaveHint, "возврат обычного текста");
                }
                finally
                {
                    Loc.LanguageOverride = null;
                    Plugin.UpdateRewards(_ => previous);
                }
            });
        }

        [Fact]
        public void QuickSuccessiveChanges_RestartTheSequence()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    Plugin.UpdateRewards(r => r.WithKnown(new[] { new RewardInfo { id = "a", title = "A" } }));
                    using var form = OpenForm();
                    var box = FindCheckBox(form);

                    box.Checked = true;
                    WaitFor(() => HintOf(form) == Loc.SavedHint, "первое сохранение");
                    box.Checked = false;   // ещё одно изменение, пока висит «сохранено»

                    Assert.Equal(Loc.SavingHint, HintOf(form));
                    WaitFor(() => HintOf(form) == Loc.SavedHint, "второе сохранение");
                    WaitFor(() => HintOf(form) == Loc.AutoSaveHint, "возврат обычного текста");
                }
                finally
                {
                    Loc.LanguageOverride = null;
                    Plugin.UpdateRewards(_ => previous);
                }
            });
        }

        [Fact]
        public void FailedSave_ShowsAnErrorThatStaysUntilTheNextChange()
        {
            RunSta(() =>
            {
                string good = _fx.ConfigPath;
                try
                {
                    using var form = OpenForm();
                    // путь, по которому записать нельзя: папки нет
                    Plugin.ConfigPathOverride = Path.Combine(_fx.Dir, "нет_такой_папки", "config.json");

                    form.SwitchTheme(false);
                    WaitFor(() => HintOf(form) == Loc.SaveFailedHint, "сообщение об ошибке");
                    Thread.Sleep(700);
                    Application.DoEvents();
                    Assert.Equal(Loc.SaveFailedHint, HintOf(form));   // само не пропадает

                    Plugin.ConfigPathOverride = good;
                    form.SwitchTheme(true);
                    WaitFor(() => HintOf(form) == Loc.SavedHint, "успешное сохранение после ошибки");
                }
                finally
                {
                    Plugin.ConfigPathOverride = good;
                    Loc.LanguageOverride = null;
                    Plugin.SetTheme(true);
                }
            });
        }

        private static ThemedCheckBox FindCheckBox(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child is ThemedCheckBox box) return box;
                var nested = FindCheckBoxOrNull(child);
                if (nested != null) return nested;
            }
            throw new InvalidOperationException("галочка не найдена");
        }

        private static ThemedCheckBox? FindCheckBoxOrNull(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child is ThemedCheckBox box) return box;
                var nested = FindCheckBoxOrNull(child);
                if (nested != null) return nested;
            }
            return null;
        }
    }
}

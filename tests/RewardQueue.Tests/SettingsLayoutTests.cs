using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace RewardQueue.Tests
{
    // Окно настроек должно переживать любой размер и любое состояние входа: карточки не шире окна,
    // содержимое не выходит за карточки
    public class SettingsLayoutTests : IClassFixture<PluginFixture>
    {
        private readonly PluginFixture _fx;

        public SettingsLayoutTests(PluginFixture fixture) => _fx = fixture;

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

        private static IEnumerable<CardPanel> Cards(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child is CardPanel card) yield return card;
                foreach (var nested in Cards(child)) yield return nested;
            }
        }

        public static IEnumerable<object[]> AuthStates()
        {
            yield return new object[] { "signed-out" };
            yield return new object[] { "waiting" };
            yield return new object[] { "signed-in" };
            yield return new object[] { "problem-signed-out" };
            yield return new object[] { "problem-signed-in" };
        }

        private static AuthSnapshot Snapshot(string name) => name switch
        {
            "waiting" => new AuthSnapshot(AuthState.WaitingForUser, UserCode: "ABCDEFGH",
                VerificationUri: "https://www.twitch.tv/activate?device-code=ABCDEFGH", CodeExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30)),
            "signed-in" => new AuthSnapshot(AuthState.SignedIn, Login: "streamer_with_a_long_nickname", UserId: "1"),
            "problem-signed-out" => new AuthSnapshot(Problem: AuthProblem.MissingScope),
            "problem-signed-in" => new AuthSnapshot(AuthState.SignedIn, Login: "streamer", UserId: "1", Problem: AuthProblem.SessionExpired),
            _ => new AuthSnapshot()
        };

        [Theory]
        [MemberData(nameof(AuthStates))]
        public void Cards_StayInsideWindow_AtAnySizeAndAuthState(string authState)
        {
            foreach (bool russian in new[] { true, false })
            {
                foreach (bool dark in new[] { true, false })
                {
                    CheckLayout(russian, dark, authState);
                }
            }
        }

        private void CheckLayout(bool russian, bool dark, string authState)
        {
            RunSta(() =>
            {
                Loc.LanguageOverride = russian;
                var previousAuth = TwitchAuth.Current;
                var previousRewards = Plugin.Rewards;
                using var auth = new TwitchAuth(Path.Combine(_fx.Dir, "layout_auth.bin"));
                try
                {
                    Plugin.SetTheme(dark);
                    // награды с короткими и очень длинными названиями; часть отмечена
                    Plugin.UpdateRewards(r => r.WithKnown(new[]
                    {
                        new RewardInfo { id = "a", title = "Заказ техники" },
                        new RewardInfo { id = "b", title = "Заказ музыки с очень длинным названием, которое не помещается в строку окна настроек" },
                        new RewardInfo { id = "c", title = "x" }
                    }).WithTracked("a", true).WithTracked("b", true).AddQueue());
                    // вторая очередь с длинным названием и наградой, лимиты заданы
                    string second = Plugin.Rewards.Queues[1].id;
                    Plugin.UpdateRewards(r => r.MoveReward("b", second).RenameQueue(second, "Очень длинное название очереди, которое не помещается в одну строку окна").SetShowLimit(second, 5).WithShowTotal(12));
                    auth.SetSnapshotForPreview(Snapshot(authState));
                    TwitchAuth.Current = auth;

                    using var form = new SettingsForm();
                    _ = form.Handle;
                    form.Show();

                    foreach (var (w, h) in new[] { (520, 360), (600, 700), (820, 660), (1500, 900), (540, 500), (520, 360) })
                    {
                        form.ClientSize = new Size(Ui.Px(w), Ui.Px(h));
                        form.PerformLayout();
                        Application.DoEvents();

                        // подпись про автосохранение закреплена внизу окна и видна при любой прокрутке
                        var bottom = form.Controls.OfType<Panel>().Single(c => c.Dock == DockStyle.Bottom);
                        Assert.Equal(form.ClientSize.Height, bottom.Bottom);
                        Assert.Equal(Loc.AutoSaveHint, bottom.Controls.OfType<Label>().Single().Text);
                        // прокручиваемая область кончается там, где начинается полоса, и подпись помещается целиком
                        var scroll = form.Controls.OfType<Panel>().Single(c => c.Dock == DockStyle.Fill);
                        Assert.Equal(bottom.Top, scroll.Bottom);
                        var hint = bottom.Controls.OfType<Label>().Single();
                        int needed = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(hint.Width - hint.Padding.Horizontal, int.MaxValue),
                            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
                        Assert.True(bottom.Height >= needed, $"подпись внизу обрезана: {w}x{h} ru={russian} полоса {bottom.Height}, нужно {needed}");

                        var cards = Cards(form).ToList();
                        Assert.Equal(5, cards.Count);
                        foreach (var card in cards)
                        {
                            string where = $"{w}x{h} ru={russian} dark={dark} auth={authState} «{card.Text}»";
                            Assert.True(card.Left >= 0 && card.Right <= form.ClientSize.Width, $"карточка вышла за окно: {where}");

                            foreach (Control child in card.Controls)
                            {
                                if (!child.Visible) continue;   // скрытые элементы места не занимают
                                Assert.True(child.Left >= 0 && child.Right <= card.Width, $"элемент вышел за карточку по ширине: {where}, {child.GetType().Name} {child.Bounds}");
                                Assert.True(child.Bottom <= card.Height, $"элемент вышел за карточку по высоте: {where}, {child.GetType().Name} {child.Bounds}");
                            }
                        }

                        // карточки идут друг под другом без наложения
                        var ordered = cards.OrderBy(c => c.Top).ToList();
                        for (int i = 1; i < ordered.Count; i++)
                        {
                            Assert.True(ordered[i].Top >= ordered[i - 1].Bottom, $"карточки наложились: {w}x{h} auth={authState}");
                        }
                    }
                }
                finally
                {
                    Plugin.UpdateRewards(_ => previousRewards);
                    TwitchAuth.Current = previousAuth;
                    Loc.LanguageOverride = null;
                    Plugin.SetTheme(true);
                }
            });
        }

        // Карточка входа: зелёная, пока всё хорошо, бледно-красная при проблеме, обычная в остальных случаях
        [Theory]
        [InlineData("signed-out", "Normal")]
        [InlineData("waiting", "Normal")]
        [InlineData("signed-in", "Success")]
        [InlineData("problem-signed-out", "Danger")]
        [InlineData("problem-signed-in", "Danger")]
        public void TwitchCard_ColorFollowsTheLoginState(string authState, string expectedTone)
        {
            var expected = Enum.Parse<CardTone>(expectedTone);
            foreach (bool dark in new[] { true, false })
            {
                RunSta(() =>
                {
                    var previousAuth = TwitchAuth.Current;
                    using var auth = new TwitchAuth(Path.Combine(_fx.Dir, "tone_auth.bin"));
                    try
                    {
                        Plugin.SetTheme(dark);
                        auth.SetSnapshotForPreview(Snapshot(authState));
                        TwitchAuth.Current = auth;

                        using var form = new SettingsForm();
                        _ = form.Handle;
                        form.Show();

                        var card = Cards(form).OrderBy(c => c.Top).First();
                        Assert.Equal(expected, card.Tone);
                        Color fill = expected switch
                        {
                            CardTone.Success => ObsTheme.SuccessBg,
                            CardTone.Danger => ObsTheme.DangerBg,
                            _ => ObsTheme.BaseBg
                        };
                        Assert.Equal(fill, card.BackColor);
                        // подписи внутри карточки на том же фоне, иначе они проступали бы прямоугольниками
                        foreach (var label in card.Controls.OfType<Label>()) Assert.Equal(fill, label.BackColor);

                        // другие карточки оттенок не меняют
                        Assert.All(Cards(form).OrderBy(c => c.Top).Skip(1), c => Assert.Equal(CardTone.Normal, c.Tone));
                    }
                    finally
                    {
                        TwitchAuth.Current = previousAuth;
                        Plugin.SetTheme(true);
                    }
                });
            }
        }

        [Theory]
        [InlineData("https://www.twitch.tv/activate?device-code=ABCD", true)]
        [InlineData("https://twitch.tv/activate", true)]
        [InlineData("http://www.twitch.tv/activate", false)]
        [InlineData("https://evil.example/twitch.tv", false)]
        [InlineData("https://nottwitch.tv/activate", false)]
        [InlineData("file:///C:/Windows/System32/calc.exe", false)]
        [InlineData("calc.exe", false)]
        [InlineData("", false)]
        public void LoginPage_OnlyTwitchAddressesAreOpened(string uri, bool expected)
        {
            Assert.Equal(expected, SettingsForm.IsTwitchUri(uri));
        }
    }
}

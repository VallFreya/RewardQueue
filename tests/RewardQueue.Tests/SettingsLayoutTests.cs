using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace RewardQueue.Tests
{
    // Окно настроек должно переживать любой размер: карточки не шире окна, содержимое не выходит за карточки
    public class SettingsLayoutTests : IClassFixture<PluginFixture>
    {
        public SettingsLayoutTests(PluginFixture fixture) { }

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

        [Theory]
        [InlineData(true, true)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void Cards_StayInsideWindow_AtAnySize(bool russian, bool dark)
        {
            RunSta(() =>
            {
                Loc.LanguageOverride = russian;
                try
                {
                    Plugin.SetTheme(dark);
                    using var form = new SettingsForm();
                    _ = form.Handle;
                    form.Show();

                    foreach (var (w, h) in new[] { (520, 360), (600, 700), (820, 660), (1500, 900), (540, 500), (520, 360) })
                    {
                        form.ClientSize = new Size(Ui.Px(w), Ui.Px(h));
                        form.PerformLayout();
                        Application.DoEvents();

                        var cards = Cards(form).ToList();
                        Assert.Equal(2, cards.Count);
                        foreach (var card in cards)
                        {
                            string where = $"{w}x{h} ru={russian} dark={dark} «{card.Text}»";
                            Assert.True(card.Left >= 0 && card.Right <= form.ClientSize.Width, $"карточка вышла за окно: {where}");

                            foreach (Control child in card.Controls)
                            {
                                Assert.True(child.Left >= 0 && child.Right <= card.Width, $"элемент вышел за карточку по ширине: {where}, {child.GetType().Name} {child.Bounds}");
                                Assert.True(child.Bottom <= card.Height, $"элемент вышел за карточку по высоте: {where}, {child.GetType().Name} {child.Bounds}");
                            }
                        }

                        // карточки идут друг под другом без наложения
                        Assert.True(cards[1].Top >= cards[0].Bottom, $"карточки наложились: {w}x{h}");
                    }
                }
                finally
                {
                    Loc.LanguageOverride = null;
                    Plugin.SetTheme(true);
                }
            });
        }
    }
}

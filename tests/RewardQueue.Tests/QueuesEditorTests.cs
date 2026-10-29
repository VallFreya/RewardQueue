using System.Drawing;
using System.Windows.Forms;
using Xunit;

namespace RewardQueue.Tests
{
    // Блок «Очереди» в окне настроек: добавление, название, лимиты, удаление, перетаскивание наград и очередей
    public class QueuesEditorTests : IClassFixture<PluginFixture>
    {
        public QueuesEditorTests(PluginFixture fixture) => _ = fixture;

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

        private static void Pump(int ms = 150)
        {
            var end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }

        private static IEnumerable<T> All<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                if (child is T match) yield return match;
                foreach (var nested in All<T>(child)) yield return nested;
            }
        }

        private static List<QueuePanel> Panels(Form form) => All<QueuePanel>(form).OrderBy(p => p.Parent!.Top + p.Top).ToList();

        // Нажатие кнопки как от мыши (у собственных кнопок нет PerformClick)
        private static void Press(Control control) =>
            typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(control, new object[] { EventArgs.Empty });

        private static Point Center(Control control) => control.PointToScreen(new Point(control.Width / 2, control.Height / 2));

        // Награды a, b, c, d отмечены и лежат в первой очереди
        private static void SetUpRewards()
        {
            Plugin.UpdateRewards(_ => RewardSettings.Empty
                .WithKnown(new[] { "a", "b", "c", "d" }.Select(id => new RewardInfo { id = id, title = "Награда " + id }))
                .WithTracked("a", true).WithTracked("b", true).WithTracked("c", true).WithTracked("d", true));
        }

        private SettingsForm Open()
        {
            Loc.LanguageOverride = true;
            Plugin.SetTheme(true);
            SetUpRewards();
            var form = new SettingsForm { SavingHintMs = 50, SavedHintMs = 100 };
            _ = form.Handle;
            form.ClientSize = new Size(Ui.Px(820), Ui.Px(1100));
            form.Show();
            Pump();
            return form;
        }

        private static void Cleanup(RewardSettings previous)
        {
            Loc.LanguageOverride = null;
            Plugin.UpdateRewards(_ => previous);
        }

        [Fact]
        public void ByDefault_OneUnnamedQueueWithoutDeleteButton()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();

                    var panels = Panels(form);
                    Assert.Single(panels);
                    Assert.Equal("", panels[0].NameBox.Text);
                    Assert.False(panels[0].DeleteButton.Visible);
                    Assert.Equal(4, panels[0].Chips.Count);
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void AddQueue_AppendsAnEmptyQueueAndEnablesDeleteButtons()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    var add = All<ThemedButton>(form).Single(b => b.Text == Loc.AddQueue);

                    Press(add);
                    Pump();

                    Assert.Equal(2, Plugin.Rewards.Queues.Count);
                    var panels = Panels(form);
                    Assert.Equal(2, panels.Count);
                    Assert.Empty(panels[1].Chips);
                    Assert.All(panels, p => Assert.True(p.DeleteButton.Visible));
                    Assert.Equal(4, panels[0].Chips.Count);   // награды остались в первой очереди
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void NameAndLimit_AreSavedAfterAShortPause_AndNameMayStayEmpty()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    var panel = Panels(form).Single();

                    panel.NameBox.Text = "Танки";
                    panel.LimitBox.Value = 3;
                    form.FlushEdits();

                    Assert.Equal("Танки", Plugin.Rewards.Queues[0].name);
                    Assert.Equal(3, Plugin.Rewards.Queues[0].show_limit);

                    panel.NameBox.Text = "";
                    form.FlushEdits();
                    Assert.Equal("", Plugin.Rewards.Queues[0].name);
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void ShowTotal_IsSavedAndZeroMeansNoLimit()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    var stepper = All<NumberStepper>(form).Single(s => s.Parent is CardPanel && s.Parent.Controls.OfType<QueuePanel>().Any());

                    Assert.Equal(0, stepper.Value);
                    stepper.Value = 7;
                    form.FlushEdits();
                    Assert.Equal(7, Plugin.Rewards.ShowTotal);

                    stepper.Value = 0;
                    form.FlushEdits();
                    Assert.Equal(0, Plugin.Rewards.ShowTotal);
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void DraggingARewardToAnotherQueue_MovesIt()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    Plugin.UpdateRewards(r => r.AddQueue());
                    // пересобираем окно под две очереди
                    form.RebuildUi();
                    Pump();

                    var panels = Panels(form);
                    Assert.Equal(2, panels.Count);
                    var chip = panels[0].Chips.Single(c => c.RewardId == "c");

                    form.BeginDrag(SettingsForm.DragKind.Reward, "c", chip.Text);
                    form.MoveDrag(Center(panels[1]));
                    Assert.True(panels[1].DropHot);
                    form.EndDrag(Center(panels[1]), apply: true);
                    Pump();

                    Assert.Equal(Plugin.Rewards.Queues[1].id, Plugin.Rewards.QueueFor("c"));
                    Assert.Equal(Plugin.Rewards.Queues[0].id, Plugin.Rewards.QueueFor("a"));
                    var rebuilt = Panels(form);
                    Assert.Equal(new[] { "c" }, rebuilt[1].Chips.Select(c => c.RewardId).ToArray());
                    Assert.All(rebuilt, p => Assert.False(p.DropHot));
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void DroppingARewardOnItsOwnQueue_ChangesNothing()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    var panel = Panels(form).Single();
                    var before = Plugin.Rewards;

                    form.BeginDrag(SettingsForm.DragKind.Reward, "a", "Награда a");
                    form.MoveDrag(Center(panel));
                    Assert.False(panel.DropHot);
                    form.EndDrag(Center(panel), apply: true);
                    Pump();

                    Assert.Same(before, Plugin.Rewards);
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void DroppingAReward_OutsideAnyQueueOrWithEscape_ChangesNothing()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    Plugin.UpdateRewards(r => r.AddQueue());
                    form.RebuildUi();
                    Pump();
                    var panels = Panels(form);
                    var before = Plugin.Rewards;

                    // мимо очередей
                    form.BeginDrag(SettingsForm.DragKind.Reward, "a", "Награда a");
                    form.EndDrag(form.PointToScreen(new Point(2, 2)), apply: true);
                    Assert.Same(before, Plugin.Rewards);

                    // отмена
                    form.BeginDrag(SettingsForm.DragKind.Reward, "a", "Награда a");
                    form.MoveDrag(Center(panels[1]));
                    form.CancelDrag();
                    Assert.False(panels[1].DropHot);
                    Assert.Same(before, Plugin.Rewards);
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void DraggingAQueue_ChangesTheOrder()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    Plugin.UpdateRewards(r => r.AddQueue().AddQueue());
                    form.RebuildUi();
                    Pump();
                    var panels = Panels(form);
                    string[] ids = Plugin.Rewards.Queues.Select(q => q.id).ToArray();

                    // третью очередь тянем выше центра первой: она становится первой
                    form.BeginDrag(SettingsForm.DragKind.Queue, ids[2], "Очередь 3");
                    var above = Center(panels[0]);
                    above.Y -= panels[0].Height / 2 - 2;
                    form.MoveDrag(above);
                    form.EndDrag(above, apply: true);
                    Pump();

                    Assert.Equal(new[] { ids[2], ids[0], ids[1] }, Plugin.Rewards.Queues.Select(q => q.id).ToArray());
                    Assert.Equal(new[] { ids[2], ids[0], ids[1] }, Panels(form).Select(p => p.QueueId).ToArray());
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void DraggingAQueueToItsOwnPlace_ChangesNothing()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    Plugin.UpdateRewards(r => r.AddQueue());
                    form.RebuildUi();
                    Pump();
                    var panels = Panels(form);
                    var before = Plugin.Rewards;

                    form.BeginDrag(SettingsForm.DragKind.Queue, panels[0].QueueId, "Очередь 1");
                    var point = Center(panels[0]);
                    form.MoveDrag(point);
                    form.EndDrag(point, apply: true);
                    Pump();

                    Assert.Same(before, Plugin.Rewards);
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void DeletingAQueue_MovesItsRewardsToTheFirstAndKeepsTheLastQueue()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    // две очереди по две награды
                    Plugin.UpdateRewards(r => { var two = r.AddQueue(); return two.MoveReward("c", two.Queues[1].id).MoveReward("d", two.Queues[1].id); });
                    form.RebuildUi();
                    Pump();
                    var panels = Panels(form);
                    Assert.Equal(2, panels[0].Chips.Count);
                    Assert.Equal(2, panels[1].Chips.Count);

                    Press(panels[1].DeleteButton);
                    Pump();

                    Assert.Single(Plugin.Rewards.Queues);
                    Assert.Equal(new[] { "a", "b", "c", "d" }, Plugin.Rewards.Queues[0].reward_ids.ToArray());
                    var rebuilt = Panels(form);
                    Assert.Single(rebuilt);
                    Assert.Equal(4, rebuilt[0].Chips.Count);
                    Assert.False(rebuilt[0].DeleteButton.Visible);   // последнюю очередь удалить нельзя
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void TextTypedInAFieldIsKeptWhenTheWindowRebuilds()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    Panels(form).Single().NameBox.Text = "Музыка";   // ещё не записано (пауза не прошла)
                    var add = All<ThemedButton>(form).Single(b => b.Text == Loc.AddQueue);

                    Press(add);   // пересборка окна не должна потерять набранное
                    Pump();

                    Assert.Equal("Музыка", Plugin.Rewards.Queues[0].name);
                    Assert.Equal("Музыка", Panels(form)[0].NameBox.Text);
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void UntickingAReward_RemovesItFromWhicheverQueueHoldsItAndRefreshesTheBlock()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    Plugin.UpdateRewards(r => { var two = r.AddQueue(); return two.MoveReward("c", two.Queues[1].id); });
                    form.RebuildUi();
                    Pump();
                    Assert.Equal(new[] { "c" }, Panels(form)[1].Chips.Select(c => c.RewardId).ToArray());

                    // галочка у награды «c» (она лежит во второй очереди)
                    var box = All<ThemedCheckBox>(form).Single(b => b.Text == "Награда c");
                    box.Checked = false;
                    Pump();

                    Assert.False(Plugin.Rewards.IsTracked("c"));
                    Assert.All(Plugin.Rewards.Queues, q => Assert.DoesNotContain("c", q.reward_ids));
                    // и на экране плашки «c» нет ни в одной очереди
                    Assert.All(Panels(form), p => Assert.DoesNotContain("c", p.Chips.Select(c => c.RewardId)));
                    Assert.Empty(Panels(form)[1].Chips);
                }
                finally { Cleanup(previous); }
            });
        }

        [Fact]
        public void TickingAReward_PutsItIntoTheFirstQueueOnScreen()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    using var form = Open();
                    All<ThemedCheckBox>(form).Single(b => b.Text == "Награда d").Checked = false;
                    Pump();
                    Assert.DoesNotContain("d", Panels(form).Single().Chips.Select(c => c.RewardId));

                    All<ThemedCheckBox>(form).Single(b => b.Text == "Награда d").Checked = true;
                    Pump();

                    Assert.Contains("d", Panels(form)[0].Chips.Select(c => c.RewardId));
                    Assert.Equal(Plugin.Rewards.FirstQueueId, Plugin.Rewards.QueueFor("d"));
                }
                finally { Cleanup(previous); }
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void QueuePanelColor_DiffersFromTheInputsSoTheyStayVisible(bool dark)
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    Plugin.SetTheme(dark);
                    ObsTheme.Apply(dark);
                    var panel = new QueuePanel(new QueueConfig { id = "x" }, Array.Empty<(string, string)>(), false, new ToolTip());

                    int Diff(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                    Assert.True(Diff(panel.BackColor, ObsTheme.InputBg) >= 30, $"поля сливаются с панелью: {panel.BackColor} и {ObsTheme.InputBg}");
                    // панель голубоватая: синего в ней больше, чем красного
                    Assert.True(panel.BackColor.B > panel.BackColor.R);
                }
                finally
                {
                    Plugin.SetTheme(true);
                    Cleanup(previous);
                }
            });
        }

        [Fact]
        public void ScrollingPanel_DoesNotScrollToTheControlThatGetsFocus()
        {
            RunSta(() =>
            {
                static (Panel Panel, Button Far) Make(Panel panel)
                {
                    panel.AutoScroll = true;
                    panel.Size = new Size(300, 200);
                    var near = new Button { Location = new Point(10, 10), Size = new Size(80, 30) };
                    var far = new Button { Location = new Point(10, 1500), Size = new Size(80, 30) };
                    panel.Controls.Add(near);
                    panel.Controls.Add(far);
                    return (panel, far);
                }

                using var form = new Form();
                var (ordinary, ordinaryFar) = Make(new Panel());
                var (ours, oursFar) = Make(new ScrollPanel());
                form.Controls.Add(ordinary);
                form.Controls.Add(ours);
                ours.Left = 320;
                form.Show();
                Pump();

                // обычная панель при фокусе уезжает к кнопке (так вёл себя щелчок по плашке), наша остаётся на месте
                ordinaryFar.Focus();
                oursFar.Focus();
                Pump();
                Assert.NotEqual(0, ordinary.AutoScrollPosition.Y);
                Assert.Equal(0, ours.AutoScrollPosition.Y);
            });
        }

        [Fact]
        public void ClosingTheWindow_SavesPendingEdits()
        {
            RunSta(() =>
            {
                var previous = Plugin.Rewards;
                try
                {
                    var form = Open();
                    Panels(form).Single().LimitBox.Value = 9;

                    form.Close();
                    form.Dispose();

                    Assert.Equal(9, Plugin.Rewards.Queues[0].show_limit);
                }
                finally { Cleanup(previous); }
            });
        }
    }
}

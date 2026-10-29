using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using RewardQueue;

namespace UiPreview
{
    // Открывает окно настроек на самом большом мониторе, меняет размер, тему и делает снимки экрана.
    // Аргументы: папка для снимков, затем сценарии вида "ширинаxвысота[:light][:состояние входа]"
    // (размер клиентской области в логических пикселях; состояния входа: out, waiting, in, problem, problem-in)
    internal static class Harness
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();

            string outDir = args[0];
            Directory.CreateDirectory(outDir);
            string tmp = Path.Combine(Path.GetTempPath(), "RewardQueueUiTest");
            Directory.CreateDirectory(tmp);
            Plugin.ConfigPathOverride = Path.Combine(tmp, "config.json");
            Plugin.LogEnabled = false;
            Loc.LanguageOverride = Environment.GetEnvironmentVariable("UITEST_LANG") != "en";
            Plugin.EnsureAccessKey();
            LocalServer.StartCurrent(47999, Plugin.AccessKey, new QueueApi());
            // награды для снимка: короткие и длинные названия, часть отмечена; UITEST_REWARDS=0 показывает пустой список
            if (Environment.GetEnvironmentVariable("UITEST_REWARDS") != "0")
            {
                Plugin.UpdateRewards(r => r.WithKnown(new[]
                {
                    new RewardInfo { id = "a", title = "Заказ техники" },
                    new RewardInfo { id = "b", title = "Заказ музыки" },
                    new RewardInfo { id = "c", title = "Очень длинное название награды, которое придётся обрезать многоточием в конце строки" }
                }).WithTracked("a", true).WithTracked("b", true).WithTracked("c", true));

                // UITEST_QUEUES=1: одна очередь; по умолчанию две, у второй название и лимит
                if (Environment.GetEnvironmentVariable("UITEST_QUEUES") != "1")
                {
                    Plugin.UpdateRewards(r =>
                    {
                        var two = r.AddQueue();
                        string second = two.Queues[1].id;
                        return two.MoveReward("c", second).RenameQueue(second, "Музыка").SetShowLimit(second, 3);
                    });
                }
            }
            using var auth = new TwitchAuth(Path.Combine(tmp, "preview_auth.bin"));
            TwitchAuth.Current = auth;

            // по умолчанию самый большой монитор; UITEST_MONITOR=<номер> выбирает другой
            var screens = Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++) Console.WriteLine($"monitor {i}: {screens[i].Bounds}");
            string? pick = Environment.GetEnvironmentVariable("UITEST_MONITOR");
            var screen = pick != null ? screens[int.Parse(pick)] : screens.OrderByDescending(s => s.Bounds.Width * s.Bounds.Height).First();
            using var form = new SettingsForm();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(screen.WorkingArea.X + 100, screen.WorkingArea.Y + 60);
            form.Show();
            Pump(600);

            if (args.Length > 1 && args[1] == "dragtest")
            {
                RunDragTest(form);
                LocalServer.StopCurrent();
                return;
            }

            int n = 0;
            foreach (string scenario in args.Skip(1))
            {
                string[] parts = scenario.Split(':');
                string[] size = parts[0].Split('x');
                bool dark = !parts.Contains("light");
                auth.SetSnapshotForPreview(AuthSnapshotFor(parts));
                form.SwitchTheme(dark);
                // UITEST_HINT=saving|saved: показывает подпись внизу в этом состоянии (интервалы длинные, чтобы успеть снять)
                string? hint = Environment.GetEnvironmentVariable("UITEST_HINT");
                if (hint != null)
                {
                    form.SavingHintMs = hint == "saving" ? 60000 : 1;
                    form.SavedHintMs = 60000;
                    form.TriggerSaveHintForPreview();
                }
                float k = form.DeviceDpi / 96f;
                form.ClientSize = new Size((int)(int.Parse(size[0]) * k), (int)(int.Parse(size[1]) * k));
                Pump(700);

                string file = Path.Combine(outDir, $"{++n:00}_{string.Join('_', parts)}.png");
                Capture(form, file);
                Console.WriteLine($"{file} dpi={form.DeviceDpi} client={form.ClientSize.Width}x{form.ClientSize.Height}");
            }

            LocalServer.StopCurrent();
        }

        private static AuthSnapshot AuthSnapshotFor(string[] parts) =>
            parts.Contains("waiting") ? new AuthSnapshot(AuthState.WaitingForUser, UserCode: "ABCDEFGH",
                VerificationUri: "https://www.twitch.tv/activate?device-code=ABCDEFGH", CodeExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30))
            : parts.Contains("in") ? new AuthSnapshot(AuthState.SignedIn, Login: "streamer", UserId: "1")
            : parts.Contains("problem-in") ? new AuthSnapshot(AuthState.SignedIn, Login: "streamer", UserId: "1", Problem: AuthProblem.Offline)
            : parts.Contains("problem") ? new AuthSnapshot(Problem: AuthProblem.SessionExpired)
            : new AuthSnapshot();

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        private const uint LeftDown = 0x0002, LeftUp = 0x0004;

        private static IEnumerable<T> All<T>(Control root) where T : Control
        {
            foreach (Control child in root.Controls)
            {
                if (child is T match) yield return match;
                foreach (var nested in All<T>(child)) yield return nested;
            }
        }

        // Настоящие события мыши: взять награду «Заказ техники» (первая очередь) и бросить на вторую очередь, затем поменять очереди местами
        private static void RunDragTest(SettingsForm form)
        {
            form.ClientSize = new Size(form.ClientSize.Width, (int)(1500 * form.DeviceDpi / 96f));
            Pump(500);
            form.Activate();
            Pump(300);

            var panels = All<QueuePanel>(form).OrderBy(p => p.Parent!.Top + p.Top).ToList();
            var chip = panels[0].Chips.First();
            string rewardId = chip.RewardId;
            string secondQueue = panels[1].QueueId;
            Console.WriteLine($"before: reward {rewardId} in {(Plugin.Rewards.QueueFor(rewardId) == Plugin.Rewards.FirstQueueId ? "first" : "other")} queue");

            void Move(Point screen) { SetCursorPos(screen.X, screen.Y); Pump(60); }
            Point Center(Control c) => c.PointToScreen(new Point(c.Width / 2, c.Height / 2));

            // награда: нажать, сдвинуться (по шагам), отпустить над второй очередью
            var from = Center(chip);
            var to = Center(panels[1]);
            Move(from);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Pump(80);
            for (int i = 1; i <= 10; i++) Move(new Point(from.X + (to.X - from.X) * i / 10, from.Y + (to.Y - from.Y) * i / 10));
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Pump(600);
            Console.WriteLine($"after reward drag: reward {rewardId} in {(Plugin.Rewards.QueueFor(rewardId) == secondQueue ? "SECOND" : "not second")} queue");

            // очередь: взять ручку второй очереди и поднять выше первой
            panels = All<QueuePanel>(form).OrderBy(p => p.Parent!.Top + p.Top).ToList();
            string movedId = panels[1].QueueId;
            var handle = Center(panels[1].Handle);
            var top = panels[0].PointToScreen(new Point(panels[0].Width / 2, 4));
            Move(handle);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Pump(80);
            for (int i = 1; i <= 10; i++) Move(new Point(handle.X + (top.X - handle.X) * i / 10, handle.Y + (top.Y - handle.Y) * i / 10));
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Pump(600);
            Console.WriteLine($"after queue drag: first queue is {(Plugin.Rewards.FirstQueueId == movedId ? "the dragged one" : "unchanged")}");
        }

        private static void Pump(int ms)
        {
            var end = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < end) { Application.DoEvents(); Thread.Sleep(15); }
        }

        private static void Capture(Form form, string file)
        {
            form.Activate();
            Pump(150);
            var r = form.Bounds;
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save(file, ImageFormat.Png);
        }
    }
}

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
            LocalServer.StartCurrent(47999, Plugin.AccessKey);
            // награды для снимка: короткие и длинные названия, часть отмечена; UITEST_REWARDS=0 показывает пустой список
            if (Environment.GetEnvironmentVariable("UITEST_REWARDS") != "0")
            {
                Plugin.UpdateRewards(r => r.WithKnown(new[]
                {
                    new RewardInfo { id = "a", title = "Заказ техники" },
                    new RewardInfo { id = "b", title = "Заказ музыки" },
                    new RewardInfo { id = "c", title = "Очень длинное название награды, которое придётся обрезать многоточием в конце строки" }
                }).WithTracked("a", true));
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

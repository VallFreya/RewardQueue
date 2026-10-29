using System.Drawing;
using System.Windows.Forms;

namespace RewardQueue
{
    /// <summary>
    /// Окно настроек. Пока в нём адреса страниц с пояснением, как вставить их в OBS, и состояние сервера.
    /// Внешний вид повторяет тему OBS (см. ObsTheme); тема выбирается справа сверху и запоминается в config.json.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const int CardPad = 16;

        private readonly System.Windows.Forms.Timer _liveTimer;
        // Эти элементы пересоздаются при смене темы (см. BuildUi)
        private ToolTip _tip = null!;
        private Label _serverLabel = null!;
        private Label _clientsLabel = null!;
        private Label _probeLabel = null!;
        private ThemedButton _probeButton = null!;

        public SettingsForm()
        {
            Ui.K = DeviceDpi / 96f;

            AutoScaleMode = AutoScaleMode.None;
            Text = Loc.WindowTitle;
            ShowIcon = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            ClientSize = new Size(Math.Min(Ui.Px(820), area.Width - Ui.Px(40)), Math.Min(Ui.Px(660), area.Height - Ui.Px(100)));
            MinimumSize = SizeFromClientSize(new Size(Ui.Px(600), Ui.Px(420)));

            // состояние меняется редко, полсекунды хватает
            _liveTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _liveTimer.Tick += (s, e) => UpdateLive();

            ObsTheme.Apply(Plugin.ThemeDark);
            BuildUi();
            _liveTimer.Start();
        }

        // Строит весь интерфейс по текущей теме. Вызывается при открытии окна и при смене темы
        private void BuildUi()
        {
            BackColor = ObsTheme.WindowBg;
            ForeColor = ObsTheme.Text;
            Font = Ui.Font();
            _tip = ThemedToolTip.Create();

            // Верхняя полоса с выбором темы
            var topBar = new Panel { Dock = DockStyle.Top, Height = Ui.Px(52), BackColor = ObsTheme.WindowBg };
            var themeBox = new Panel { Dock = DockStyle.Right, Width = Ui.Px(250), BackColor = ObsTheme.WindowBg };
            themeBox.Controls.Add(new Label
            {
                Text = Loc.Theme,
                Bounds = new Rectangle(0, Ui.Px(10), Ui.Px(70), Ui.Px(32)),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = ObsTheme.TextMuted,
                BackColor = ObsTheme.WindowBg
            });
            var themePicker = new ThemedPicker(editable: false) { Bounds = new Rectangle(Ui.Px(78), Ui.Px(10), Ui.Px(156), Ui.Px(32)) };
            themePicker.SetItems(new object[] { Loc.ThemeDarkName, Loc.ThemeLightName });
            themePicker.SelectedIndex = ObsTheme.IsDark ? 0 : 1;
            themePicker.SelectionChanged += (s, e) =>
            {
                bool dark = themePicker.SelectedIndex == 0;
                // перестраиваем окно уже после того, как закончится обработка клика по списку
                BeginInvoke(new Action(() => SwitchTheme(dark)));
            };
            themeBox.Controls.Add(themePicker);
            topBar.Controls.Add(themeBox);

            // Содержимое прокручивается, если окно мало
            var content = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = ObsTheme.WindowBg };
            content.HandleCreated += (s, e) => Ui.ApplyScrollbarTheme(content.Handle);

            int cardWidth = ClientSize.Width - Ui.Px(CardPad * 2) - SystemInformation.VerticalScrollBarWidth;

            var addresses = new CardPanel
            {
                Text = Loc.AddressesTitle,
                Bounds = new Rectangle(Ui.Px(CardPad), Ui.Px(4), cardWidth, Ui.Px(360)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            content.Controls.Add(addresses);

            AddText(addresses, Loc.AddressesIntro, 44, 40, muted: true);
            AddText(addresses, Loc.DockLabel, 92, 22, bold: true);
            AddAddressRow(addresses, "panel", 116);
            AddText(addresses, Loc.DockHow, 156, 44, muted: true);
            AddText(addresses, Loc.OverlayLabel, 208, 22, bold: true);
            AddAddressRow(addresses, "overlay", 232);
            AddText(addresses, Loc.OverlayHow, 272, 44, muted: true);
            AddText(addresses, Loc.SecretWarning, 322, 24, muted: false);

            var status = new CardPanel
            {
                Text = Loc.StatusTitle,
                Bounds = new Rectangle(Ui.Px(CardPad), addresses.Bottom + Ui.Px(12), cardWidth, Ui.Px(160)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            content.Controls.Add(status);

            _serverLabel = AddText(status, "", 44, 24);
            _clientsLabel = AddText(status, "", 70, 24);

            _probeButton = new ThemedButton
            {
                Text = Loc.ProbeButton,
                Bounds = new Rectangle(Ui.Px(CardPad), Ui.Px(104), Ui.Px(260), Ui.Px(32))
            };
            _probeButton.Click += (s, e) => StartProbe();
            status.Controls.Add(_probeButton);

            _probeLabel = new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(Ui.Px(CardPad + 272), Ui.Px(104), cardWidth - Ui.Px(CardPad * 2 + 272), Ui.Px(32)),
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = ObsTheme.BaseBg,
                ForeColor = ObsTheme.TextMuted,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            status.Controls.Add(_probeLabel);

            // Fill добавляется первым: при docking контролы раскладываются от последнего добавленного к первому
            Controls.Add(content);
            Controls.Add(topBar);

            UpdateLive();
        }

        // Меняет тему: запоминает её в конфиге и пересобирает окно
        internal void SwitchTheme(bool dark)
        {
            if (dark == ObsTheme.IsDark) return;

            Plugin.SetTheme(dark);
            ObsTheme.Apply(dark);

            _liveTimer.Stop();
            SuspendLayout();
            var old = new List<Control>();
            foreach (Control control in Controls) old.Add(control);
            Controls.Clear();
            foreach (var control in old) control.Dispose();
            _tip.Dispose();

            BuildUi();
            ResumeLayout(true);

            Ui.ApplyWindowChrome(Handle);
            Invalidate(true);
            _liveTimer.Start();
        }

        private Label AddText(Control parent, string text, int top, int height, bool muted = false, bool bold = false)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = false,
                Bounds = new Rectangle(Ui.Px(CardPad), Ui.Px(top), parent.Width - Ui.Px(CardPad * 2), Ui.Px(height)),
                BackColor = ObsTheme.BaseBg,
                ForeColor = muted ? ObsTheme.TextMuted : ObsTheme.Text,
                Font = bold ? Ui.Font(FontStyle.Bold) : Ui.Font(),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            parent.Controls.Add(label);
            return label;
        }

        // Строка «адрес + кнопка Копировать». Адрес собирается при каждом нажатии из текущих порта и ключа
        private void AddAddressRow(Control parent, string page, int top)
        {
            int buttonWidth = Ui.Px(140);
            int rowWidth = parent.Width - Ui.Px(CardPad * 2);

            var box = new ThemedTextBox
            {
                Bounds = new Rectangle(Ui.Px(CardPad), Ui.Px(top), rowWidth - buttonWidth - Ui.Px(8), Ui.Px(32)),
                Text = ServerGuard.BuildUrl(Plugin.Port, Plugin.AccessKey, page),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            box.InnerTextBox.ReadOnly = true;
            parent.Controls.Add(box);

            var button = new ThemedButton
            {
                Text = Loc.Copy,
                Glyph = Ui.GlyphCopy,
                Bounds = new Rectangle(Ui.Px(CardPad) + rowWidth - buttonWidth, Ui.Px(top), buttonWidth, Ui.Px(32)),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            button.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(ServerGuard.BuildUrl(Plugin.Port, Plugin.AccessKey, page));
                    ShowCopied(button);
                }
                catch (Exception ex)
                {
                    // буфер обмена может быть занят другой программой
                    Plugin.LogWarn($"Could not copy the address: {ex.Message}");
                }
            };
            parent.Controls.Add(button);
        }

        private static void ShowCopied(ThemedButton button)
        {
            button.Text = Loc.Copied;
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (s, e) =>
            {
                timer.Dispose();
                if (!button.IsDisposed) button.Text = Loc.Copy;
            };
            timer.Start();
        }

        private static void StartProbe()
        {
            _ = Task.Run(EventSubProbe.RunAsync);
        }

        private void UpdateLive()
        {
            var server = LocalServer.Current;
            _serverLabel.Text = server != null
                ? Loc.ServerRunning(server.Port)
                : Loc.ServerFailed(LocalServer.StartError == "port is busy" ? Loc.PortBusy : LocalServer.StartError ?? "-");
            _clientsLabel.Text = Loc.OpenPages(server?.ClientCount ?? 0);

            string? result = EventSubProbe.LastResult;
            bool running = EventSubProbe.Running;
            _probeButton.Enabled = !running;
            _probeLabel.Text = running ? Loc.ProbeRunning
                : result == null ? Loc.ProbeNotRun
                : result.StartsWith("ok", StringComparison.Ordinal) ? Loc.ProbeOk
                : Loc.ProbeFailed(result);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.ApplyWindowChrome(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _liveTimer.Dispose();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

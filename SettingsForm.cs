using System.Drawing;
using System.Windows.Forms;

namespace RewardQueue
{
    /// <summary>
    /// Окно настроек. Пока в нём адреса страниц с пояснением, как вставить их в OBS, и состояние сервера.
    /// Внешний вид повторяет тему OBS (см. ObsTheme); тема выбирается справа сверху и запоминается в config.json.
    /// Раскладка считается кодом (LayoutContent): ширина карточек берётся от окна, высота каждого текста измеряется
    /// с учётом переноса, поэтому окно можно растягивать и сужать.
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private const int CardPad = 16;
        private const int CardGap = 12;
        // Окно не уже этого размера (логические пиксели): дальше строки адресов становятся бесполезно короткими
        private const int MinClientWidth = 520;
        private const int MinClientHeight = 360;

        private readonly System.Windows.Forms.Timer _liveTimer;
        // Эти элементы пересоздаются при смене темы и масштаба (см. BuildUi)
        private ToolTip _tip = null!;
        private Panel _content = null!;
        private readonly List<Card> _cards = new List<Card>();
        private Label _serverLabel = null!;
        private Label _clientsLabel = null!;
        private Label _probeLabel = null!;
        private ThemedButton _probeButton = null!;
        private float _builtScale;

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
            MinimumSize = SizeFromClientSize(new Size(Ui.Px(MinClientWidth), Ui.Px(MinClientHeight)));

            // состояние меняется редко, полсекунды хватает
            _liveTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _liveTimer.Tick += (s, e) => UpdateLive();

            ObsTheme.Apply(Plugin.ThemeDark);
            BuildUi();
            _liveTimer.Start();
        }

        // Строит весь интерфейс по текущей теме и масштабу. Вызывается при открытии окна, смене темы и смене масштаба
        private void BuildUi()
        {
            _builtScale = Ui.K;
            _cards.Clear();

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

            // Содержимое прокручивается по вертикали, если окно невысокое
            _content = new Panel { Dock = DockStyle.Fill, BackColor = ObsTheme.WindowBg };
            var content = _content;
            // Горизонтальной прокрутки нет: ширину карточек подгоняет LayoutContent. Без этого после сужения окна
            // полоса остаётся от прежней ширины
            content.HorizontalScroll.Enabled = false;
            content.HorizontalScroll.Visible = false;
            content.HorizontalScroll.Maximum = 0;
            content.AutoScroll = true;
            content.HandleCreated += (s, e) => Ui.ApplyScrollbarTheme(content.Handle);
            content.SizeChanged += (s, e) => LayoutContent();

            var addresses = AddCard(Loc.AddressesTitle);
            AddText(addresses, Loc.AddressesIntro, 0, muted: true);
            AddText(addresses, Loc.DockLabel, 16, bold: true);
            AddAddressRow(addresses, "panel", 6);
            AddText(addresses, Loc.DockHow, 6, muted: true);
            AddText(addresses, Loc.OverlayLabel, 16, bold: true);
            AddAddressRow(addresses, "overlay", 6);
            AddText(addresses, Loc.OverlayHow, 6, muted: true);
            AddText(addresses, Loc.SecretWarning, 14);

            var status = AddCard(Loc.StatusTitle);
            _serverLabel = AddText(status, "", 0);
            _clientsLabel = AddText(status, "", 6);
            AddProbeRow(status, 10);

            // Fill добавляется первым: при docking контролы раскладываются от последнего добавленного к первому
            Controls.Add(content);
            Controls.Add(topBar);

            UpdateLive();
            LayoutContent();
        }

        // Меняет тему: запоминает её в конфиге и пересобирает окно
        internal void SwitchTheme(bool dark)
        {
            if (dark == ObsTheme.IsDark) return;

            Plugin.SetTheme(dark);
            ObsTheme.Apply(dark);
            RebuildUi();
        }

        private void RebuildUi()
        {
            _liveTimer.Stop();
            SuspendLayout();
            var old = new List<Control>();
            foreach (Control control in Controls) old.Add(control);
            Controls.Clear();
            foreach (var control in old) control.Dispose();
            _tip.Dispose();

            BuildUi();
            ResumeLayout(true);

            if (IsHandleCreated) Ui.ApplyWindowChrome(Handle);
            Invalidate(true);
            _liveTimer.Start();
        }

        // Масштаб экрана известен только когда у окна есть дескриптор: если окно открылось на мониторе с другим
        // масштабом, чем при построении, подгоняем размер и пересобираем интерфейс
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Ui.ApplyWindowChrome(Handle);
            ApplyScale(DeviceDpi / 96f, resizeWindow: true);
        }

        // Окно перетащили на монитор с другим масштабом: ОС уже изменила размер окна, интерфейс пересобираем
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyScale(e.DeviceDpiNew / 96f, resizeWindow: false);
        }

        private void ApplyScale(float scale, bool resizeWindow)
        {
            if (Math.Abs(scale - _builtScale) < 0.01f) return;

            if (resizeWindow)
            {
                float ratio = scale / _builtScale;
                ClientSize = new Size((int)(ClientSize.Width * ratio), (int)(ClientSize.Height * ratio));
            }

            Ui.K = scale;
            MinimumSize = SizeFromClientSize(new Size(Ui.Px(MinClientWidth), Ui.Px(MinClientHeight)));
            RebuildUi();
        }

        // ---- раскладка ----

        // Карточка: заголовок и столбец элементов. Элемент получает верхнюю границу и ширину, возвращает нижнюю границу
        private sealed class Card
        {
            public readonly CardPanel Panel;
            private readonly List<Func<int, int, int>> _items = new List<Func<int, int, int>>();

            public Card(string title) => Panel = new CardPanel { Text = title };

            public void Add(Func<int, int, int> item) => _items.Add(item);

            public int Layout(int width)
            {
                int inner = width - Ui.Px(CardPad * 2);
                int top = Ui.Px(44);
                foreach (var item in _items) top = item(top, inner);

                Panel.Size = new Size(width, top + Ui.Px(CardPad));
                return Panel.Height;
            }
        }

        private Card AddCard(string title)
        {
            var card = new Card(title);
            _cards.Add(card);
            _content.Controls.Add(card.Panel);
            return card;
        }

        // Карточки занимают всю ширину окна. Ширину берём у окна, а не у панели: только что созданная панель свою ширину ещё не знает.
        // Место под полосу прокрутки резервируется всегда, чтобы раскладка не прыгала при её появлении
        private void LayoutContent()
        {
            if (_content == null || _cards.Count == 0) return;

            int width = Math.Max(Ui.Px(300), ClientSize.Width - Ui.Px(CardPad * 2) - SystemInformation.VerticalScrollBarWidth);
            int scrollTop = -_content.AutoScrollPosition.Y;

            _content.SuspendLayout();
            int y = Ui.Px(4);
            foreach (var card in _cards)
            {
                int height = card.Layout(width);
                card.Panel.Location = new Point(Ui.Px(CardPad), y - scrollTop);
                y += height + Ui.Px(CardGap);
            }
            _content.AutoScrollMinSize = new Size(0, y + Ui.Px(CardPad));
            _content.ResumeLayout(false);
        }

        // Подпись с переносом по словам: высота измеряется по ширине карточки
        private Label AddText(Card card, string text, int gap, bool muted = false, bool bold = false)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = false,
                UseMnemonic = false,
                BackColor = ObsTheme.BaseBg,
                ForeColor = muted ? ObsTheme.TextMuted : ObsTheme.Text,
                Font = bold ? Ui.Font(FontStyle.Bold) : Ui.Font()
            };
            card.Panel.Controls.Add(label);
            card.Add((top, width) =>
            {
                top += Ui.Px(gap);
                int height = MeasureHeight(label, width);
                label.SetBounds(Ui.Px(CardPad), top, width, height);
                return top + height;
            });
            return label;
        }

        private static int MeasureHeight(Label label, int width)
        {
            if (label.Text.Length == 0) return Ui.Px(20);

            const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int height = TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, width), int.MaxValue), flags).Height;
            return height + Ui.Px(4);
        }

        // Строка «адрес + кнопка Копировать». Адрес собирается при каждом нажатии из текущих порта и ключа
        private void AddAddressRow(Card card, string page, int gap)
        {
            var box = new ThemedTextBox { Text = ServerGuard.BuildUrl(Plugin.Port, Plugin.AccessKey, page) };
            box.InnerTextBox.ReadOnly = true;
            card.Panel.Controls.Add(box);

            var button = new ThemedButton { Text = Loc.Copy, Glyph = Ui.GlyphCopy };
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
            card.Panel.Controls.Add(button);

            card.Add((top, width) =>
            {
                top += Ui.Px(gap);
                int buttonWidth = Ui.Px(140);
                int height = Ui.Px(32);
                box.SetBounds(Ui.Px(CardPad), top, Math.Max(Ui.Px(80), width - buttonWidth - Ui.Px(8)), height);
                button.SetBounds(Ui.Px(CardPad) + width - buttonWidth, top, buttonWidth, height);
                return top + height;
            });
        }

        // Кнопка проверки связи и результат: рядом, а если места мало, результат переносится под кнопку
        private void AddProbeRow(Card card, int gap)
        {
            _probeButton = new ThemedButton { Text = Loc.ProbeButton };
            _probeButton.Click += (s, e) => StartProbe();
            card.Panel.Controls.Add(_probeButton);

            _probeLabel = new Label
            {
                AutoSize = false,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = ObsTheme.BaseBg,
                ForeColor = ObsTheme.TextMuted,
                Font = Ui.Font()
            };
            card.Panel.Controls.Add(_probeLabel);

            var button = _probeButton;
            var label = _probeLabel;
            card.Add((top, width) =>
            {
                top += Ui.Px(gap);
                int buttonWidth = Ui.Px(260);
                int height = Ui.Px(32);
                button.SetBounds(Ui.Px(CardPad), top, Math.Min(buttonWidth, width), height);

                int sideWidth = width - buttonWidth - Ui.Px(12);
                if (sideWidth >= Ui.Px(200))
                {
                    label.SetBounds(Ui.Px(CardPad) + buttonWidth + Ui.Px(12), top, sideWidth, height);
                    return top + height;
                }

                int below = top + height + Ui.Px(8);
                int labelHeight = MeasureHeight(label, width);
                label.SetBounds(Ui.Px(CardPad), below, width, labelHeight);
                return below + labelHeight;
            });
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
            bool changed = false;
            changed |= SetText(_serverLabel, server != null
                ? Loc.ServerRunning(server.Port)
                : Loc.ServerFailed(LocalServer.StartError == "port is busy" ? Loc.PortBusy : LocalServer.StartError ?? "-"));
            changed |= SetText(_clientsLabel, Loc.OpenPages(server?.ClientCount ?? 0));

            string? result = EventSubProbe.LastResult;
            bool running = EventSubProbe.Running;
            _probeButton.Enabled = !running;
            changed |= SetText(_probeLabel, running ? Loc.ProbeRunning
                : result == null ? Loc.ProbeNotRun
                : result.StartsWith("ok", StringComparison.Ordinal) ? Loc.ProbeOk
                : Loc.ProbeFailed(result));

            // новый текст мог занять другое число строк
            if (changed) LayoutContent();
        }

        private static bool SetText(Label label, string text)
        {
            if (label.Text == text) return false;
            label.Text = text;
            return true;
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

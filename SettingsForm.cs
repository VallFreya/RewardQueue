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
        private Panel _bottomBar = null!;
        private Label _bottomLabel = null!;

        // Блок «Очереди»: правки полей копятся и записываются после паузы, структурные изменения (добавить, удалить, перенести) сразу
        private readonly List<QueuePanel> _queuePanels = new List<QueuePanel>();
        private NumberStepper _showTotal = null!;
        private readonly List<Func<RewardSettings, RewardSettings>> _pendingEdits = new List<Func<RewardSettings, RewardSettings>>();
        private readonly System.Windows.Forms.Timer _editTimer = new System.Windows.Forms.Timer { Interval = 500 };

        // Перетаскивание мышью (без OLE): наград между очередями и самих очередей
        internal enum DragKind { None, Reward, Queue }

        /// <summary>Окно-«призрак» перетаскиваемого блока (null, если ничего не перетаскивают); для проверок</summary>
        internal Form? DragGhost => _ghost;
        private DragKind _dragKind;
        private string _dragId = "";
        private string _dragSourceQueue = "";
        private DragGhostForm? _ghost;
        private Control? _lifted;
        private Point _grabOffset;
        private QueuePanel? _dragHot;
        private int _dragInsertIndex = -1;
        private Panel? _insertLine;
        private Control? _armedControl;
        private DragKind _armedKind;
        private string _armedId = "";
        private string _armedText = "";
        private Point _armedAt;
        private readonly System.Windows.Forms.Timer _dragScrollTimer = new System.Windows.Forms.Timer { Interval = 40 };

        // Подпись внизу: обычный текст → «Сохраняю…» → «Настройки сохранены» (3 секунды) → обычный текст
        private enum SaveHint { Idle, Saving, Saved, Failed }
        private readonly System.Windows.Forms.Timer _hintTimer = new System.Windows.Forms.Timer();
        private SaveHint _hint = SaveHint.Idle;
        private bool _pendingSaveOk = true;
        // 0: ничего, 1: фон сохранил настройки, 2: фон не смог сохранить (список наград обновляется не в потоке окна)
        private volatile int _externalSave;
        /// <summary>Сколько показывается «Сохраняю…» и «Настройки сохранены» (в проверках сокращается)</summary>
        internal int SavingHintMs = 400;
        internal int SavedHintMs = 3000;
        private readonly List<Card> _cards = new List<Card>();
        private Label _serverLabel = null!;
        private Label _clientsLabel = null!;
        private Label _ordersLabel = null!;
        private Label _queueLabel = null!;
        private Label _rewardsStatus = null!;
        private Label _rewardsTracked = null!;
        private ThemedButton _rewardsRefresh = null!;
        // Загрузка списка наград идёт в фоне: результат и просьба перестроить окно читаются таймером окна
        private volatile bool _fetching;
        private volatile bool _rebuildRequested;
        private volatile string _fetchMessage = "";
        private Label _authStatus = null!;
        private CardPanel _twitchCard = null!;
        private Label _authCode = null!;
        private ThemedButton _authOpen = null!;
        private ThemedButton _authCopy = null!;
        private ThemedButton _authAction = null!;
        private string _authShown = "";
        // Скрытые элементы. Control.Visible в ещё не показанном окне всегда false, поэтому раскладка опирается на этот список
        private readonly HashSet<Control> _hidden = new HashSet<Control>();
        private float _builtScale;

        public SettingsForm()
        {
            Ui.K = DeviceDpi / 96f;

            AutoScaleMode = AutoScaleMode.None;
            Text = Loc.WindowTitle;
            ShowIcon = false;
            MaximizeBox = true;   // окно можно развернуть кнопкой или перетаскиванием к краю экрана
            StartPosition = FormStartPosition.CenterScreen;

            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            ClientSize = new Size(Math.Min(Ui.Px(820), area.Width - Ui.Px(40)), Math.Min(Ui.Px(660), area.Height - Ui.Px(100)));
            MinimumSize = SizeFromClientSize(new Size(Ui.Px(MinClientWidth), Ui.Px(MinClientHeight)));

            // состояние меняется редко, полсекунды хватает
            _hintTimer.Tick += (s, e) => OnHintTimer();
            _editTimer.Tick += (s, e) => FlushEdits();
            _dragScrollTimer.Tick += (s, e) => AutoScrollWhileDragging();
            _liveTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _liveTimer.Tick += (s, e) =>
            {
                // список наград обновился: строки с галочками строятся заново
                int external = Interlocked.Exchange(ref _externalSave, 0);
                if (external != 0) ShowSaving(external == 1);

                if (_rebuildRequested)
                {
                    _rebuildRequested = false;
                    RebuildUi();
                    return;
                }
                UpdateLive();
            };

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
            _content = new ScrollPanel { Dock = DockStyle.Fill, BackColor = ObsTheme.WindowBg };
            var content = _content;
            // Горизонтальной прокрутки нет: ширину карточек подгоняет LayoutContent. Без этого после сужения окна
            // полоса остаётся от прежней ширины
            content.HorizontalScroll.Enabled = false;
            content.HorizontalScroll.Visible = false;
            content.HorizontalScroll.Maximum = 0;
            content.AutoScroll = true;
            content.HandleCreated += (s, e) => Ui.ApplyScrollbarTheme(content.Handle);
            content.SizeChanged += (s, e) => LayoutContent();

            _authShown = "";
            _hidden.Clear();
            var twitch = AddCard(Loc.TwitchTitle);
            _twitchCard = twitch.Panel;
            _authStatus = AddText(twitch, "", 0);
            _authCode = AddText(twitch, "", 10, bold: true, scale: 1.8f);
            AddAuthButtons(twitch, 12);
            AddText(twitch, Loc.TwitchPrivacy, 12, muted: true);

            var rewards = AddCard(Loc.RewardsTitle);
            AddText(rewards, Loc.RewardsIntro, 0, muted: true);
            AddRewardsRefresh(rewards, 12);
            foreach (var reward in Plugin.Rewards.Known) AddRewardRow(rewards, reward);
            if (Plugin.Rewards.Known.Count == 0) AddText(rewards, Loc.RewardsEmpty, 12, muted: true);
            _rewardsTracked = AddText(rewards, "", 12, muted: true);

            _queuePanels.Clear();
            _insertLine = null;
            var queues = AddCard(Loc.QueuesTitle);
            AddText(queues, Loc.QueuesIntro, 0, muted: true);
            AddQueuesEditor(queues, 12);
            AddShowTotal(queues, 14);

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
            _ordersLabel = AddText(status, "", 6);
            _queueLabel = AddText(status, "", 6);

            // Нижняя полоса видна всегда, как бы ни была прокручена страница: кнопки «Сохранить» нет, всё сохраняется само
            _bottomBar = new Panel { Dock = DockStyle.Bottom, Height = Ui.Px(36), BackColor = ObsTheme.BaseBg };
            var bottomBar = _bottomBar;
            _bottomLabel = new Label
            {
                Text = HintText(),
                Dock = DockStyle.Fill,
                AutoSize = false,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(Ui.Px(CardPad), 0, Ui.Px(CardPad), 0),
                ForeColor = ObsTheme.TextMuted,
                BackColor = ObsTheme.BaseBg
            };
            bottomBar.Controls.Add(_bottomLabel);

            FitBottomBar();

            // Fill добавляется первым: при docking контролы раскладываются от последнего добавленного к первому
            Controls.Add(content);
            Controls.Add(topBar);
            Controls.Add(bottomBar);

            UpdateLive();
            LayoutContent();
        }

        // Меняет тему: запоминает её в конфиге и пересобирает окно
        internal void SwitchTheme(bool dark)
        {
            if (dark == ObsTheme.IsDark) return;

            ShowSaving(Plugin.SetTheme(dark));
            ObsTheme.Apply(dark);
            RebuildUi();
        }

        internal void RebuildUi()
        {
            // введённое в поля не должно потеряться при пересборке
            FlushEdits();
            CancelDrag();
            int scroll = _content == null ? 0 : -_content.AutoScrollPosition.Y;

            // На время пересборки окно не перерисовывается: иначе виден каждый промежуточный кадр (белая вспышка)
            _liveTimer.Stop();
            bool redraw = IsHandleCreated;
            if (redraw) SendMessage(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            try
            {
                SuspendLayout();
                var old = new List<Control>();
                foreach (Control control in Controls) old.Add(control);
                Controls.Clear();
                foreach (var control in old) control.Dispose();
                _tip.Dispose();

                BuildUi();
                ResumeLayout(true);
                if (scroll > 0) _content.AutoScrollPosition = new Point(0, scroll);
            }
            finally
            {
                if (redraw) SendMessage(Handle, WmSetRedraw, (IntPtr)1, IntPtr.Zero);
            }

            if (IsHandleCreated) Ui.ApplyWindowChrome(Handle);
            Invalidate(true);
            _liveTimer.Start();
        }

        private const int WmSetRedraw = 0x000B;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

        // ---- подпись про сохранение ----

        private string HintText() => _hint switch
        {
            SaveHint.Saving => Loc.SavingHint,
            SaveHint.Saved => Loc.SavedHint,
            SaveHint.Failed => Loc.SaveFailedHint,
            _ => Loc.AutoSaveHint
        };

        // Изменение внесено: показываем «Сохраняю…», затем результат. Настройки пишутся сразу (синхронно), а подпись
        // держится заметное время, чтобы её можно было прочитать
        private void ShowSaving(bool saved)
        {
            _pendingSaveOk = saved;
            SetHint(SaveHint.Saving);
            _hintTimer.Stop();
            _hintTimer.Interval = Math.Max(1, SavingHintMs);
            _hintTimer.Start();
        }

        /// <summary>Запускает подпись «Сохраняю…» без настоящего изменения (для снимков окна)</summary>
        internal void TriggerSaveHintForPreview() => ShowSaving(true);

        private void OnHintTimer()
        {
            _hintTimer.Stop();
            switch (_hint)
            {
                case SaveHint.Saving:
                    if (_pendingSaveOk)
                    {
                        SetHint(SaveHint.Saved);
                        // через три секунды возвращается обычный текст
                        _hintTimer.Interval = Math.Max(1, SavedHintMs);
                        _hintTimer.Start();
                    }
                    else
                    {
                        // ошибка остаётся на экране до следующего изменения
                        SetHint(SaveHint.Failed);
                    }
                    break;
                case SaveHint.Saved:
                    SetHint(SaveHint.Idle);
                    break;
            }
        }

        private void SetHint(SaveHint hint)
        {
            _hint = hint;
            if (_bottomLabel == null) return;

            _bottomLabel.Text = HintText();
            FitBottomBar();
        }

        // Высоту полосы подгоняем после раскладки окна, а не внутри неё: изменение высоты во время прохода раскладки
        // не доходит до остальных элементов, и прокручиваемая область остаётся под полосой
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_bottomLabel != null) FitBottomBar();
        }

        // Нижняя полоса выше, если подпись в узком окне переносится на вторую строку
        private void FitBottomBar()
        {
            const TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            // с запасом: Label переносит текст чуть раньше, чем его измеряет TextRenderer без внутренних отступов
            int textWidth = Math.Max(1, ClientSize.Width - Ui.Px(CardPad * 2) - Ui.Px(14));
            int textHeight = TextRenderer.MeasureText(_bottomLabel.Text, _bottomLabel.Font, new Size(textWidth, int.MaxValue), flags).Height;
            int height = Math.Max(Ui.Px(36), textHeight + Ui.Px(16));
            if (_bottomBar.Height != height) _bottomBar.Height = height;
        }

        // Подпись с переносом по словам: высота измеряется по ширине карточки
        private Label AddText(Card card, string text, int gap, bool muted = false, bool bold = false, float scale = 1f)
        {
            var label = new Label
            {
                Text = text,
                AutoSize = false,
                UseMnemonic = false,
                BackColor = ObsTheme.BaseBg,
                ForeColor = muted ? ObsTheme.TextMuted : ObsTheme.Text,
                Font = Ui.Font(bold ? FontStyle.Bold : FontStyle.Regular, scale)
            };
            card.Panel.Controls.Add(label);
            card.Add((top, width) =>
            {
                // скрытая подпись места не занимает
                if (_hidden.Contains(label)) return top;

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

        // ---- очереди ----

        // Плашки наград в очередях приводятся в соответствие с настройками; остальное в окне не трогается
        private void RefreshQueueChips()
        {
            var settings = Plugin.Rewards;
            foreach (var panel in _queuePanels)
            {
                var queue = settings.Queue(panel.QueueId);
                if (queue == null) continue;
                panel.SetRewards(queue.reward_ids.Select(id => (id, RewardTitle(id))).ToList());
            }
            LayoutContent();
        }

        private string RewardTitle(string id)
        {
            var known = Plugin.Rewards.Known.FirstOrDefault(r => r.id == id);
            return known != null && known.title.Length > 0 ? known.title : Loc.RewardMissing(id);
        }

        // Запоминает правку поля и записывает её после короткой паузы (чтобы не писать файл на каждую букву)
        private void ScheduleEdit(Func<RewardSettings, RewardSettings> change)
        {
            _pendingEdits.Add(change);
            _editTimer.Stop();
            _editTimer.Start();
            ShowSavingNow();
        }

        // Записывает накопленные правки полей. Вызывается по паузе, перед структурным изменением и при закрытии окна
        internal void FlushEdits()
        {
            _editTimer.Stop();
            if (_pendingEdits.Count == 0) return;

            var edits = _pendingEdits.ToArray();
            _pendingEdits.Clear();
            ShowSaving(Plugin.UpdateRewards(r => edits.Aggregate(r, (settings, edit) => edit(settings))));
        }

        // Правка принята, но ещё не записана: подпись внизу показывает «Сохраняю…» до записи
        private void ShowSavingNow()
        {
            _hintTimer.Stop();
            SetHint(SaveHint.Saving);
        }

        // Структурное изменение (добавить, удалить, перенести): записывается сразу, окно пересобирается под новый состав
        private void ApplyStructural(Func<RewardSettings, RewardSettings> change)
        {
            FlushEdits();
            ShowSaving(Plugin.UpdateRewards(change));
            // пересборка уничтожает элементы, из чьих обработчиков мы вызваны: делаем её после них
            BeginInvoke(new Action(RebuildUi));
        }

        // Блок очередей: кнопка «Добавить очередь» справа вверху и панели очередей одна под другой
        private void AddQueuesEditor(Card card, int gap)
        {
            var add = new ThemedButton { Text = Loc.AddQueue, Glyph = Ui.GlyphAdd, Tone = ButtonTone.Success };
            add.Click += (s, e) => ApplyStructural(r => r.AddQueue());
            card.Panel.Controls.Add(add);
            card.Add((top, width) =>
            {
                top += Ui.Px(gap);
                int buttonWidth = Math.Min(width, Math.Max(Ui.Px(150), TextRenderer.MeasureText(add.Text, add.Font).Width + Ui.Px(56)));
                add.SetBounds(Ui.Px(CardPad) + width - buttonWidth, top, buttonWidth, Ui.Px(32));
                return top + Ui.Px(32);
            });

            var settings = Plugin.Rewards;
            foreach (var queue in settings.Queues)
            {
                var rewards = queue.reward_ids.Select(id => (id, RewardTitle(id))).ToList();
                var panel = new QueuePanel(queue, rewards, canDelete: settings.Queues.Count > 1, _tip);
                string queueId = queue.id;
                panel.NameEdited += name => ScheduleEdit(r => r.RenameQueue(queueId, name));
                panel.LimitEdited += limit => ScheduleEdit(r => r.SetShowLimit(queueId, limit));
                panel.DeleteClicked += () => ApplyStructural(r => r.RemoveQueue(queueId));
                AttachDrag(panel.Handle, DragKind.Queue, queueId, () => QueueLabel(queueId));
                foreach (var chip in panel.Chips) AttachDrag(chip, DragKind.Reward, chip.RewardId, () => chip.Text);
                panel.ChipAdded += chip => AttachDrag(chip, DragKind.Reward, chip.RewardId, () => chip.Text);

                _queuePanels.Add(panel);
                card.Panel.Controls.Add(panel);
                card.Add((top, width) =>
                {
                    top += Ui.Px(8);
                    int height = panel.LayoutFor(width);
                    panel.Location = new Point(Ui.Px(CardPad), top);
                    return top + height;
                });
            }
        }

        // Общий лимит записей на трансляции по всем очередям: подпись, счётчик и пояснение про 0
        private void AddShowTotal(Card card, int gap)
        {
            var label = new Label
            {
                Text = Loc.ShowTotal,
                AutoSize = false,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = ObsTheme.Text,
                BackColor = ObsTheme.BaseBg,
                Font = Ui.Font(FontStyle.Bold)
            };
            _showTotal = new NumberStepper { ZeroPad = false, Minimum = 0, Maximum = RewardSettings.MaxShowLimit, Value = Plugin.Rewards.ShowTotal };
            _showTotal.ValueChanged += (s, e) => ScheduleEdit(r => r.WithShowTotal(_showTotal.Value));
            card.Panel.Controls.Add(label);
            card.Panel.Controls.Add(_showTotal);
            card.Add((top, width) =>
            {
                top += Ui.Px(gap);
                int labelWidth = TextRenderer.MeasureText(label.Text, label.Font).Width + Ui.Px(12);
                label.SetBounds(Ui.Px(CardPad), top, labelWidth, Ui.Px(32));
                _showTotal.SetBounds(Ui.Px(CardPad) + labelWidth + Ui.Px(8), top, Ui.Px(110), Ui.Px(32));
                return top + Ui.Px(32);
            });
            AddText(card, Loc.ShowTotalHint, 6, muted: true);
        }

        private string QueueLabel(string queueId)
        {
            var queues = Plugin.Rewards.Queues;
            int index = queues.ToList().FindIndex(q => q.id == queueId);
            string name = index >= 0 ? queues[index].name : "";
            return name.Length > 0 ? name : Loc.QueueFallbackName(index + 1);
        }

        // ---- перетаскивание ----

        // Элемент можно «взять» левой кнопкой: после небольшого сдвига начинается перетаскивание, дальше его ведёт мышь
        // (элемент держит захват мыши до отпускания кнопки)
        private void AttachDrag(Control control, DragKind kind, string id, Func<string> text)
        {
            control.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _armedControl = control;
                _armedKind = kind;
                _armedId = id;
                _armedText = text();
                _armedAt = Cursor.Position;
            };
            control.MouseMove += (s, e) =>
            {
                if (_armedControl != control) return;
                var now = Cursor.Position;
                if (_dragKind == DragKind.None)
                {
                    if (Math.Abs(now.X - _armedAt.X) < Ui.Px(4) && Math.Abs(now.Y - _armedAt.Y) < Ui.Px(4)) return;
                    BeginDrag(_armedKind, _armedId, _armedText);
                }
                MoveDrag(now);
            };
            control.MouseUp += (s, e) =>
            {
                if (_armedControl != control) return;
                _armedControl = null;
                if (_dragKind != DragKind.None) EndDrag(Cursor.Position, apply: true);
            };
        }

        internal void BeginDrag(DragKind kind, string id, string text)
        {
            ResetDragState();
            _dragKind = kind;
            _dragId = id;
            _dragSourceQueue = kind == DragKind.Reward
                ? _queuePanels.FirstOrDefault(p => p.Chips.Any(c => c.RewardId == id))?.QueueId ?? ""
                : id;

            // Под курсором летает копия всего перетаскиваемого блока (очереди целиком или плашки награды), а на его месте
            // остаётся пунктирный контур. Блок «держится» за ту точку, за которую его взяли
            Control? source = kind == DragKind.Queue
                ? _queuePanels.FirstOrDefault(p => p.QueueId == id)
                : _queuePanels.SelectMany(p => p.Chips).FirstOrDefault(c => c.RewardId == id);

            Bitmap image;
            Point origin;
            if (source != null && source.Width > 0 && source.Height > 0 && source.IsHandleCreated)
            {
                image = new Bitmap(source.Width, source.Height);
                source.DrawToBitmap(image, new Rectangle(Point.Empty, source.Size));
                origin = source.PointToScreen(Point.Empty);
                _grabOffset = _armedControl != null
                    ? new Point(_armedAt.X - origin.X, _armedAt.Y - origin.Y)
                    : new Point(source.Width / 2, source.Height / 2);
            }
            else
            {
                // блока на экране нет (проверки без окна): рисуем плашку с названием
                var size = TextRenderer.MeasureText(text, Font);
                image = new Bitmap(Math.Max(40, size.Width + Ui.Px(28)), Ui.Px(30));
                using (var g = Graphics.FromImage(image))
                {
                    g.Clear(ObsTheme.Primary);
                    TextRenderer.DrawText(g, text, Font, new Rectangle(Point.Empty, image.Size), ObsTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                origin = Cursor.Position;
                _grabOffset = new Point(image.Width / 2, image.Height / 2);
            }

            _ghost = new DragGhostForm(image) { Location = origin };
            _ghost.Show(this);

            if (source is QueuePanel queuePanel) queuePanel.Lifted = true;
            else if (source is RewardChip chip) chip.Lifted = true;
            _lifted = source;

            _edgeArmed = false;
            _dragScrollTimer.Start();
        }

        // Ведёт «призрак» за мышью и показывает, куда упадёт перетаскиваемое
        internal void MoveDrag(Point screen)
        {
            if (_dragKind == DragKind.None) return;

            if (_ghost != null) _ghost.Location = new Point(screen.X - _grabOffset.X, screen.Y - _grabOffset.Y);

            if (_dragKind == DragKind.Reward)
            {
                // награду можно бросить на любую очередь, кроме той, где она уже лежит
                var hot = _queuePanels.FirstOrDefault(p => p.RectangleToScreen(p.ClientRectangle).Contains(screen) && p.QueueId != _dragSourceQueue);
                if (_dragHot != hot)
                {
                    if (_dragHot != null) _dragHot.DropHot = false;
                    _dragHot = hot;
                    if (hot != null) hot.DropHot = true;
                }
            }
            else
            {
                UpdateInsertLine(screen);
            }
        }

        // Для очереди: новое место определяется тем, сколько остальных очередей выше курсора; место показывает линия между ними
        private void UpdateInsertLine(Point screen)
        {
            var others = _queuePanels.Where(p => p.QueueId != _dragId).ToList();
            int index = others.Count(p =>
            {
                var rect = p.RectangleToScreen(p.ClientRectangle);
                return rect.Top + rect.Height / 2 < screen.Y;
            });

            int currentIndex = _queuePanels.FindIndex(p => p.QueueId == _dragId);
            _dragInsertIndex = index == currentIndex ? -1 : index;

            _insertLine ??= new Panel { Height = Ui.Px(4), BackColor = ObsTheme.FocusRing };
            var host = others.Count > 0 ? others[0].Parent : null;
            if (_dragInsertIndex < 0 || host == null)
            {
                _insertLine.Visible = false;
                return;
            }

            if (_insertLine.Parent != host) host.Controls.Add(_insertLine);
            int y = index == 0 ? others[0].Top - Ui.Px(6) : others[index - 1].Bottom + Ui.Px(2);
            _insertLine.SetBounds(others[0].Left, y, others[0].Width, Ui.Px(4));
            _insertLine.Visible = true;
            _insertLine.BringToFront();
        }

        internal void EndDrag(Point screen, bool apply)
        {
            if (_dragKind == DragKind.None) return;

            MoveDrag(screen);
            var kind = _dragKind;
            string id = _dragId;
            var hot = _dragHot;
            int insert = _dragInsertIndex;
            CancelDrag();
            if (!apply) return;

            if (kind == DragKind.Reward && hot != null)
            {
                string target = hot.QueueId;
                ApplyStructural(r => r.MoveReward(id, target));
            }
            else if (kind == DragKind.Queue && insert >= 0)
            {
                ApplyStructural(r => r.MoveQueue(id, insert));
            }
        }

        internal void CancelDrag()
        {
            ResetDragState();
            _armedControl = null;
        }

        // Убирает «призрак», подсветку и линию вставки. «Взятый» мышью элемент не трогает: перетаскивание ведёт именно он
        private void ResetDragState()
        {
            _dragScrollTimer.Stop();
            _edgeDirection = 0;
            _edgeArmed = false;
            _dragKind = DragKind.None;
            _dragId = "";
            _dragInsertIndex = -1;
            if (_dragHot != null) _dragHot.DropHot = false;
            _dragHot = null;
            if (_insertLine != null) _insertLine.Visible = false;
            if (_ghost != null)
            {
                _ghost.Dispose();
                _ghost = null;
            }

            // перетаскиваемое возвращается на место (если окно при этом не пересобрано: тогда элемента уже нет)
            if (_lifted != null && !_lifted.IsDisposed)
            {
                if (_lifted is QueuePanel queuePanel) queuePanel.Lifted = false;
                else if (_lifted is RewardChip chip) chip.Lifted = false;
            }
            _lifted = null;
        }

        // Если держать перетаскиваемое у верхнего или нижнего края списка, список прокручивается. Только после небольшой
        // задержки у края (примерно треть секунды): сразу после того, как элемент взяли, ничего не должно двигаться
        private const int EdgeDwellMs = 350;
        private int _edgeDirection;
        private long _edgeSinceMs;
        // Самопрокрутка включается, только когда курсор после начала перетаскивания побывал вне зоны края.
        // Если элемент взяли прямо у края окна, список не должен уезжать из-под руки
        private bool _edgeArmed;

        private void AutoScrollWhileDragging()
        {
            if (_dragKind == DragKind.None || _content == null) return;

            var at = _content.PointToClient(Cursor.Position);
            int edge = Ui.Px(28), step = Ui.Px(18);
            int direction = at.Y < edge ? -1 : at.Y > _content.Height - edge ? 1 : 0;
            if (direction == 0) _edgeArmed = true;
            if (!_edgeArmed)
            {
                _edgeDirection = 0;
                return;
            }

            int current = -_content.AutoScrollPosition.Y;
            int max = Math.Max(0, _content.DisplayRectangle.Height - _content.ClientSize.Height);
            bool canScroll = direction < 0 ? current > 0 : direction > 0 && current < max;
            if (direction == 0 || !canScroll)
            {
                _edgeDirection = 0;
                return;
            }

            long now = Environment.TickCount64;
            if (direction != _edgeDirection)
            {
                _edgeDirection = direction;
                _edgeSinceMs = now;
                return;
            }
            if (now - _edgeSinceMs < EdgeDwellMs) return;

            _content.AutoScrollPosition = new Point(0, Math.Clamp(current + direction * step, 0, max));
            MoveDrag(Cursor.Position);
        }

        // Esc отменяет перетаскивание
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && _dragKind != DragKind.None)
            {
                CancelDrag();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // правки, сделанные меньше чем за паузу до закрытия, не должны потеряться
            FlushEdits();
            base.OnFormClosing(e);
        }

        // ---- награды ----

        // Кнопка «Обновить список» и результат: рядом, а если места мало, результат переносится под кнопку
        private void AddRewardsRefresh(Card card, int gap)
        {
            _rewardsRefresh = new ThemedButton { Text = Loc.RewardsRefresh, Glyph = Ui.GlyphRefresh };
            _rewardsRefresh.Click += (s, e) => _ = Task.Run(RefreshRewardsAsync);
            card.Panel.Controls.Add(_rewardsRefresh);

            _rewardsStatus = new Label
            {
                AutoSize = false,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = ObsTheme.BaseBg,
                ForeColor = ObsTheme.TextMuted,
                Font = Ui.Font()
            };
            card.Panel.Controls.Add(_rewardsStatus);

            var button = _rewardsRefresh;
            var label = _rewardsStatus;
            card.Add((top, width) =>
            {
                top += Ui.Px(gap);
                int buttonWidth = Ui.Px(200);
                int height = Ui.Px(32);
                button.SetBounds(Ui.Px(CardPad), top, Math.Min(buttonWidth, width), height);

                int sideWidth = width - buttonWidth - Ui.Px(12);
                if (sideWidth >= Ui.Px(200))
                {
                    label.SetBounds(Ui.Px(CardPad) + buttonWidth + Ui.Px(12), top, sideWidth, height);
                    return top + height;
                }

                if (label.Text.Length == 0) return top + height;
                int below = top + height + Ui.Px(8);
                int labelHeight = MeasureHeight(label, width);
                label.SetBounds(Ui.Px(CardPad), below, width, labelHeight);
                return below + labelHeight;
            });
        }

        // Строка награды с галочкой. Отметка записывается в конфиг сразу
        private void AddRewardRow(Card card, RewardInfo reward)
        {
            string id = reward.id;
            var box = new ThemedCheckBox
            {
                Text = reward.title.Length > 0 ? reward.title : id,
                Checked = Plugin.Rewards.IsTracked(id),
                BackColor = ObsTheme.BaseBg
            };
            box.CheckedChanged += (s, e) =>
            {
                ShowSaving(Plugin.UpdateRewards(r => r.WithTracked(id, box.Checked)));
                UpdateLive();
                // награда появляется в первой очереди или исчезает из той очереди, где лежала: меняются только плашки в очередях,
                // окно не пересобирается (иначе оно мигало бы)
                RefreshQueueChips();
            };
            card.Panel.Controls.Add(box);
            card.Add((top, width) =>
            {
                top += Ui.Px(6);
                int height = Ui.Px(24);
                box.SetBounds(Ui.Px(CardPad), top, width, height);
                return top + height;
            });
        }

        // Получает список наград с канала и сохраняет его. Выполняется в фоне; окно подхватывает результат таймером
        private async Task RefreshRewardsAsync()
        {
            if (_fetching) return;
            _fetching = true;
            _fetchMessage = Loc.RewardsRefreshing;
            try
            {
                var api = TwitchApi.Current;
                var result = api == null
                    ? new RewardsResult(RewardsResultKind.NotSignedIn, Array.Empty<RewardInfo>())
                    : await api.GetRewardsAsync().ConfigureAwait(false);

                if (result.Kind == RewardsResultKind.Success)
                {
                    bool saved = Plugin.UpdateRewards(r => r.WithKnown(result.Rewards));
                    _externalSave = saved ? 1 : 2;
                    _fetchMessage = result.Rewards.Count == 0 ? Loc.RewardsNoneOnChannel : Loc.RewardsLoaded(result.Rewards.Count);
                    _rebuildRequested = true;
                }
                else
                {
                    _fetchMessage = Loc.RewardsProblem(result.Kind, result.Message);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError($"Reward list refresh failed: {ex.Message}");
                _fetchMessage = Loc.RewardsProblem(RewardsResultKind.Failed, ex.Message);
            }
            finally
            {
                _fetching = false;
            }
        }

        private static void ShowCopied(ThemedButton button)
        {
            string normal = button.Text;
            button.Text = Loc.Copied;
            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += (s, e) =>
            {
                timer.Dispose();
                if (!button.IsDisposed) button.Text = normal;
            };
            timer.Start();
        }

        // ---- вход в Twitch ----

        // Кнопки входа: «Открыть страницу» и «Копировать код» (пока ждём подтверждения) и главная кнопка (Войти, Отмена или Выйти).
        // Идут в ряд, при нехватке места переносятся на следующую строку; скрытые места не занимают
        private void AddAuthButtons(Card card, int gap)
        {
            _authOpen = new ThemedButton { Text = Loc.AuthOpenPage };
            _authOpen.Click += (s, e) => OpenLoginPage();
            _authCopy = new ThemedButton { Text = Loc.AuthCopyCode, Glyph = Ui.GlyphCopy };
            _authCopy.Click += (s, e) => CopyLoginCode();
            _authAction = new ThemedButton { Text = Loc.AuthSignIn };
            _authAction.Click += (s, e) => OnAuthAction();

            var buttons = new[] { _authOpen, _authCopy, _authAction };
            foreach (var button in buttons) card.Panel.Controls.Add(button);
            SetShown(_authOpen, false);
            SetShown(_authCopy, false);

            card.Add((top, width) =>
            {
                top += Ui.Px(gap);
                int x = 0;
                int rowTop = top;
                int height = Ui.Px(32);
                foreach (var button in buttons)
                {
                    if (_hidden.Contains(button)) continue;

                    int buttonWidth = Math.Min(width, Math.Max(Ui.Px(120), TextRenderer.MeasureText(button.Text, button.Font).Width + Ui.Px(48)));
                    if (x > 0 && x + buttonWidth > width)
                    {
                        x = 0;
                        rowTop += height + Ui.Px(8);
                    }

                    button.SetBounds(Ui.Px(CardPad) + x, rowTop, buttonWidth, height);
                    x += buttonWidth + Ui.Px(8);
                }
                return rowTop + height;
            });
        }

        private void OnAuthAction()
        {
            var auth = TwitchAuth.Current;
            if (auth == null) return;

            switch (auth.Snapshot.State)
            {
                case AuthState.SignedOut:
                    // вход идёт в фоне: окно остаётся живым, состояние подхватит таймер
                    _ = Task.Run(auth.LoginAsync);
                    break;
                case AuthState.WaitingForUser:
                    auth.CancelLogin();
                    break;
                case AuthState.SignedIn:
                    auth.SignOut();
                    break;
            }
        }

        // Страницу входа открываем только если адрес похож на адрес Twitch: ответ сервера нельзя считать безопасным для запуска
        private void OpenLoginPage()
        {
            string uri = TwitchAuth.Current?.Snapshot.VerificationUri ?? "";
            if (!IsTwitchUri(uri)) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Plugin.LogWarn($"Could not open the login page: {ex.Message}");
            }
        }

        internal static bool IsTwitchUri(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps
            && (parsed.Host == "twitch.tv" || parsed.Host.EndsWith(".twitch.tv", StringComparison.Ordinal));

        private void CopyLoginCode()
        {
            string code = TwitchAuth.Current?.Snapshot.UserCode ?? "";
            if (code.Length == 0) return;

            try
            {
                Clipboard.SetText(code);
                ShowCopied(_authCopy);
            }
            catch (Exception ex)
            {
                Plugin.LogWarn($"Could not copy the code: {ex.Message}");
            }
        }

        // Показывает состояние входа. true, если что-то изменилось (раскладка могла поменяться)
        private bool UpdateAuthUi()
        {
            var auth = TwitchAuth.Current;
            var snapshot = auth?.Snapshot ?? new AuthSnapshot();
            string signature = snapshot + "|" + (auth == null);
            if (signature == _authShown) return false;
            _authShown = signature;

            string status;
            Color color = ObsTheme.Text;
            switch (snapshot.State)
            {
                case AuthState.WaitingForUser:
                    status = Loc.AuthWaiting;
                    break;
                case AuthState.SignedIn:
                    status = Loc.AuthSignedIn(snapshot.Login);
                    if (snapshot.Problem != AuthProblem.None)
                    {
                        status += "\n" + Loc.AuthProblemText(snapshot.Problem, snapshot.Detail);
                        color = ObsTheme.DangerAccent;
                    }
                    break;
                default:
                    status = snapshot.Problem == AuthProblem.None ? Loc.AuthSignedOut : Loc.AuthProblemText(snapshot.Problem, snapshot.Detail);
                    if (snapshot.Problem != AuthProblem.None) color = ObsTheme.DangerAccent;
                    break;
            }

            // Карточка зелёная, пока вход действует без замечаний, и бледно-красная, если что-то не так
            _twitchCard.Tone = snapshot.Problem != AuthProblem.None ? CardTone.Danger
                : snapshot.State == AuthState.SignedIn ? CardTone.Success
                : CardTone.Normal;

            bool waiting = snapshot.State == AuthState.WaitingForUser;
            _authStatus.Text = status;
            _authStatus.ForeColor = color;
            _authCode.Text = snapshot.UserCode;
            SetShown(_authCode, waiting);
            SetShown(_authOpen, waiting);
            SetShown(_authCopy, waiting);

            _authAction.Text = snapshot.State switch
            {
                AuthState.WaitingForUser => Loc.Cancel,
                AuthState.SignedIn => Loc.AuthSignOut,
                _ => Loc.AuthSignIn
            };
            _authAction.Primary = snapshot.State == AuthState.SignedOut;
            // «Выйти» бледно-красная: это действие, которое отменяет вход
            _authAction.Tone = snapshot.State == AuthState.SignedIn ? ButtonTone.Danger : ButtonTone.Normal;
            _authAction.Enabled = auth != null;
            _authAction.Invalidate();
            return true;
        }

        private void UpdateLive()
        {
            var server = LocalServer.Current;
            bool changed = UpdateAuthUi();
            changed |= SetText(_serverLabel, server != null
                ? Loc.ServerRunning(server.Port)
                : Loc.ServerFailed(LocalServer.StartError == "port is busy" ? Loc.PortBusy : LocalServer.StartError ?? "-"));
            changed |= SetText(_clientsLabel, Loc.OpenPages(server?.ClientCount ?? 0));

            var auth = TwitchAuth.Current;
            var client = EventSubClient.Current;
            string orders = auth == null || auth.Snapshot.State != AuthState.SignedIn || client == null ? Loc.OrdersNeedLogin
                : client.State == EventSubState.Connected ? Loc.OrdersConnected
                : client.State == EventSubState.Connecting ? Loc.OrdersConnecting
                : Loc.OrdersRetry(client.Detail);
            changed |= SetText(_ordersLabel, orders);
            changed |= SetText(_queueLabel, Loc.QueueCount(QueueService.Current?.Count ?? 0));

            var rewardSettings = Plugin.Rewards;
            string tracked = rewardSettings.Known.Count == 0 ? "" : Loc.RewardsTracked(rewardSettings.TrackedKnownCount, rewardSettings.Known.Count);
            changed |= SetText(_rewardsTracked, tracked);
            SetShown(_rewardsTracked, tracked.Length > 0);
            changed |= SetText(_rewardsStatus, _fetchMessage);
            _rewardsRefresh.Enabled = !_fetching && auth?.Snapshot.State == AuthState.SignedIn;

            // новый текст мог занять другое число строк
            if (changed) LayoutContent();
        }

        // Показывает или прячет элемент; раскладка берёт скрытые из _hidden
        private void SetShown(Control control, bool shown)
        {
            control.Visible = shown;
            if (shown) _hidden.Remove(control);
            else _hidden.Add(control);
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
                _hintTimer.Dispose();
                _editTimer.Dispose();
                _dragScrollTimer.Dispose();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

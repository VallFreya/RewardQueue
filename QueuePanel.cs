using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RewardQueue
{
    /// <summary>«Ручка» очереди: за неё очередь перетаскивается, меняясь местами с другими</summary>
    internal sealed class GripHandle : Control
    {
        public GripHandle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.SizeAll;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            // две колонки по три точки
            int dot = Math.Max(2, Ui.Px(3));
            int gapX = Ui.Px(4), gapY = Ui.Px(4);
            int totalW = dot * 2 + gapX, totalH = dot * 3 + gapY * 2;
            int x0 = (Width - totalW) / 2, y0 = (Height - totalH) / 2;
            using var brush = new SolidBrush(ObsTheme.TextMuted);
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 2; col++) g.FillEllipse(brush, x0 + col * (dot + gapX), y0 + row * (dot + gapY), dot, dot);
            }
        }
    }

    /// <summary>
    /// Область прокрутки, которая не прокручивается сама к элементу, получившему фокус. Без этого щелчок по плашке или ручке
    /// подтягивал бы её в область видимости и сдвигал всё содержимое под рукой
    /// </summary>
    internal sealed class ScrollPanel : Panel
    {
        protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;
    }

    /// <summary>Награда внутри очереди: плашка с названием, её можно перетащить в другую очередь</summary>
    internal sealed class RewardChip : Control
    {
        public string RewardId { get; }

        private bool _lifted;

        /// <summary>Плашку перетаскивают: на её месте остаётся пунктирный контур</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Lifted
        {
            get => _lifted;
            set
            {
                if (_lifted == value) return;
                _lifted = value;
                Invalidate();
            }
        }

        public RewardChip(string rewardId, string title)
        {
            RewardId = rewardId;
            Text = title;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Ui.Font(scale: 0.95f);
            Cursor = Cursors.SizeAll;
            TabStop = false;
            Height = Ui.Px(28);
        }

        /// <summary>Ширина по тексту, но не больше доступной</summary>
        public int PreferredWidthFor(int max) =>
            Math.Min(max, TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + Ui.Px(26));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            if (_lifted)
            {
                using var outline = Ui.RoundedRect(rect, rect.Height / 2f);
                using var dashed = new Pen(ObsTheme.FocusRing, 1.5f) { DashStyle = DashStyle.Dash };
                g.DrawPath(dashed, outline);
                return;
            }

            using (var path = Ui.RoundedRect(rect, rect.Height / 2f))
            using (var brush = new SolidBrush(ObsTheme.ButtonBg))
            using (var pen = new Pen(ObsTheme.ButtonBorderHover, 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.Px(12), 0, Width - Ui.Px(24), Height), ObsTheme.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Пунктирный контур на месте очереди, которую перетаскивают</summary>
    internal sealed class LiftedVeil : Control
    {
        public LiftedVeil()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(ObsTheme.BaseBg);

            var rect = new RectangleF(1.5f, 1.5f, Width - 3, Height - 3);
            using var path = Ui.RoundedRect(rect, Ui.Px(4));
            using var dashed = new Pen(ObsTheme.FocusRing, 1.5f) { DashStyle = DashStyle.Dash };
            g.DrawPath(dashed, path);
        }
    }

    /// <summary>
    /// «Призрак» перетаскиваемого: полупрозрачное окно с картинкой всего блока, которое следует за курсором.
    /// Не берёт фокус и пропускает мышь сквозь себя
    /// </summary>
    internal sealed class DragGhostForm : Form
    {
        private const int WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExTransparent = 0x20;

        private readonly Bitmap _image;

        public DragGhostForm(Bitmap image)
        {
            _image = image;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            Opacity = 0.82;
            Size = image.Size;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(_image, 0, 0);
            using var pen = new Pen(ObsTheme.FocusRing, 2f);
            e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _image.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Одна очередь в окне настроек: ручка для перетаскивания, название (необязательное), кнопка удаления, «Показывать до»
    /// и награды очереди. Раскладку считает сама (LayoutFor), как карточки окна.
    /// </summary>
    internal sealed class QueuePanel : Control
    {
        private const int Pad = 10;
        private const int Gap = 8;
        private const int RowHeight = 32;

        private readonly GripHandle _handle = new GripHandle();
        private readonly ThemedTextBox _name = new ThemedTextBox();
        private readonly ThemedButton _delete = new ThemedButton();
        private readonly Label _limitLabel = new Label();
        private readonly NumberStepper _limit = new NumberStepper();
        private readonly Label _limitHint = new Label();
        private readonly Label _emptyHint = new Label();
        private readonly List<RewardChip> _chips = new List<RewardChip>();
        private bool _dropHot;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string QueueId { get; }

        /// <summary>Очередь последняя: удалить её нельзя, кнопки удаления нет</summary>
        public bool CanDelete { get; }

        public GripHandle Handle => _handle;
        public IReadOnlyList<RewardChip> Chips => _chips;
        public ThemedTextBox NameBox => _name;
        public NumberStepper LimitBox => _limit;
        public ThemedButton DeleteButton => _delete;

        /// <summary>Название изменено пользователем (после каждой правки)</summary>
        public event Action<string>? NameEdited;

        /// <summary>«Показывать до» изменено пользователем</summary>
        public event Action<int>? LimitEdited;

        public event Action? DeleteClicked;

        /// <summary>Появилась новая плашка награды (после SetRewards): окно подключает к ней перетаскивание</summary>
        public event Action<RewardChip>? ChipAdded;

        public QueuePanel(QueueConfig queue, IReadOnlyList<(string Id, string Title)> rewards, bool canDelete, ToolTip tip)
        {
            QueueId = queue.id;
            CanDelete = canDelete;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // Фон панели бледно-голубой: белые (в тёмной теме серые) поля ввода на нём видны
            BackColor = PanelColor();
            Font = Ui.Font();

            _handle.BackColor = BackColor;
            tip.SetToolTip(_handle, Loc.QueueDragTip);

            _name.Text = queue.name;
            _name.InnerTextBox.PlaceholderText = Loc.QueueNamePlaceholder;
            _name.InnerTextBox.MaxLength = RewardSettings.MaxQueueNameLength;
            _name.InnerTextBox.TextChanged += (s, e) => NameEdited?.Invoke(_name.Text);

            _delete.Glyph = Ui.GlyphDelete;
            _delete.Tone = ButtonTone.Danger;
            _delete.Click += (s, e) => DeleteClicked?.Invoke();
            tip.SetToolTip(_delete, Loc.QueueDeleteTip);

            _limitLabel.Text = Loc.QueueShowLimit;
            _limitLabel.AutoSize = false;
            _limitLabel.UseMnemonic = false;
            _limitLabel.TextAlign = ContentAlignment.MiddleLeft;
            _limitLabel.ForeColor = ObsTheme.Text;
            _limitLabel.BackColor = BackColor;

            _limit.ZeroPad = false;
            _limit.Minimum = 0;
            _limit.Maximum = RewardSettings.MaxShowLimit;
            _limit.Value = Math.Clamp(queue.show_limit, 0, RewardSettings.MaxShowLimit);
            _limit.ValueChanged += (s, e) => LimitEdited?.Invoke(_limit.Value);

            _limitHint.Text = Loc.QueueLimitHint;
            _limitHint.AutoSize = false;
            _limitHint.UseMnemonic = false;
            _limitHint.TextAlign = ContentAlignment.MiddleLeft;
            _limitHint.ForeColor = ObsTheme.TextMuted;
            _limitHint.BackColor = BackColor;

            _emptyHint.Text = Loc.QueueEmpty;
            _emptyHint.AutoSize = false;
            _emptyHint.UseMnemonic = false;
            _emptyHint.TextAlign = ContentAlignment.MiddleLeft;
            _emptyHint.ForeColor = ObsTheme.TextMuted;
            _emptyHint.BackColor = BackColor;

            Controls.AddRange(new Control[] { _handle, _name, _delete, _limitLabel, _limit, _limitHint, _emptyHint });
            PopulateChips(rewards, raiseEvent: false);
            _delete.Visible = canDelete;
        }

        /// <summary>
        /// Заменяет награды очереди, не трогая остальное содержимое панели (поля и введённый в них текст остаются).
        /// Нужна, когда награду отметили или сняли галочку: окно при этом не пересобирается и не мигает
        /// </summary>
        public void SetRewards(IReadOnlyList<(string Id, string Title)> rewards) => PopulateChips(rewards, raiseEvent: true);

        private void PopulateChips(IReadOnlyList<(string Id, string Title)> rewards, bool raiseEvent)
        {
            foreach (var old in _chips)
            {
                Controls.Remove(old);
                old.Dispose();
            }
            _chips.Clear();

            foreach (var (id, title) in rewards)
            {
                var chip = new RewardChip(id, title) { BackColor = BackColor };
                _chips.Add(chip);
                Controls.Add(chip);
                if (raiseEvent) ChipAdded?.Invoke(chip);
            }
            _emptyHint.Visible = _chips.Count == 0;
        }

        private Control? _veil;

        /// <summary>
        /// Очередь перетаскивают: её копия летает под курсором, а на месте остаётся пустой пунктирный контур.
        /// Содержимое закрывает «завеса» поверх всего, ручка при этом сохраняет захват мыши
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Lifted
        {
            get => _veil != null;
            set
            {
                if (value == (_veil != null)) return;
                if (value)
                {
                    _veil = new LiftedVeil { Dock = DockStyle.Fill };
                    Controls.Add(_veil);
                    _veil.BringToFront();
                }
                else
                {
                    Controls.Remove(_veil);
                    _veil!.Dispose();
                    _veil = null;
                }
            }
        }

        private static Color PanelColor() => Ui.Mix(ObsTheme.BaseBg, ObsTheme.Primary, ObsTheme.IsDark ? 0.24f : 0.32f);

        /// <summary>Подсветка, когда над очередью держат награду, которую собираются в неё бросить</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool DropHot
        {
            get => _dropHot;
            set
            {
                if (_dropHot == value) return;
                _dropHot = value;
                Invalidate();
            }
        }

        /// <summary>Расставляет содержимое для заданной ширины и возвращает нужную высоту</summary>
        public int LayoutFor(int width)
        {
            int pad = Ui.Px(Pad), gap = Ui.Px(Gap), row = Ui.Px(RowHeight);
            int inner = Math.Max(Ui.Px(120), width - pad * 2);

            // строка 1: ручка, название, удаление
            int y = pad;
            int handleW = Ui.Px(22);
            int deleteW = CanDelete ? row : 0;
            _handle.SetBounds(pad, y, handleW, row);
            int nameLeft = pad + handleW + gap;
            int nameRight = pad + inner - (CanDelete ? deleteW + gap : 0);
            _name.SetBounds(nameLeft, y, Math.Max(Ui.Px(80), nameRight - nameLeft), row);
            if (CanDelete) _delete.SetBounds(pad + inner - deleteW, y, deleteW, row);

            // строка 2: «Показывать до» и счётчик, подсказка рядом или ниже
            y += row + gap;
            int labelW = TextRenderer.MeasureText(_limitLabel.Text, _limitLabel.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(8);
            _limitLabel.SetBounds(pad, y, labelW, row);
            int stepperW = Ui.Px(110);
            _limit.SetBounds(pad + labelW + gap, y, stepperW, row);
            int hintLeft = pad + labelW + gap + stepperW + gap;
            int hintW = TextRenderer.MeasureText(_limitHint.Text, _limitHint.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(6);
            if (hintLeft + hintW <= pad + inner)
            {
                _limitHint.SetBounds(hintLeft, y, hintW, row);
                y += row;
            }
            else
            {
                y += row;
                _limitHint.SetBounds(pad, y, inner, row);
                y += row;
            }

            // награды: плашки в несколько рядов
            y += gap;
            if (_chips.Count == 0)
            {
                _emptyHint.SetBounds(pad, y, inner, Ui.Px(26));
                y += Ui.Px(26);
            }
            else
            {
                int x = 0, rowTop = y, chipH = Ui.Px(28);
                foreach (var chip in _chips)
                {
                    int w = chip.PreferredWidthFor(inner);
                    if (x > 0 && x + w > inner)
                    {
                        x = 0;
                        rowTop += chipH + Ui.Px(6);
                    }
                    chip.SetBounds(pad + x, rowTop, w, chipH);
                    x += w + Ui.Px(6);
                }
                y = rowTop + chipH;
            }

            int height = y + pad;
            Size = new Size(width, height);
            return height;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using var path = Ui.RoundedRect(rect, Ui.Px(4));
            using var fill = new SolidBrush(BackColor);
            using var border = new Pen(_dropHot ? ObsTheme.FocusRing : Ui.Mix(ObsTheme.CardBorder, ObsTheme.FocusRing, 0.45f), _dropHot ? 2f : 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
    }
}

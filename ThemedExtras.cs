using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace RewardQueue
{
    // Контролы, которых нет у соседнего плагина: число со стрелками (для времени), галочка и значок предупреждения.

    /// <summary>
    /// Число с единицей измерения и стрелками вверх/вниз: «05 мин». Меняется щелчком по стрелкам (при удержании повторяется),
    /// колёсиком и клавишами вверх/вниз, а при желании числом можно и ввести.
    /// </summary>
    internal sealed class NumberStepper : FramedControl
    {
        private const int RepeatDelayMs = 400;
        private const int RepeatEveryMs = 70;

        private readonly TextBox _box;
        private readonly Timer _repeat = new Timer();
        private int _value;
        private int _minimum;
        private int _maximum = 99;
        private string _suffix = "";
        private int _hotArrow;      // 1 вверх, -1 вниз, 0 мышь не над стрелками
        private int _pressedArrow;
        private bool _updatingText;

        public event EventHandler? ValueChanged;

        /// <summary>Пользователь закончил правку: сменил число стрелками, колёсиком или клавишей либо ушёл из поля</summary>
        public event EventHandler? Committed;

        public NumberStepper()
        {
            _box = new TextBox { BorderStyle = BorderStyle.None, Font = Font, MaxLength = 3, TextAlign = HorizontalAlignment.Right };
            Controls.Add(_box);
            HookChild(_box);
            _box.TextChanged += (s, e) => OnBoxTextChanged();
            _box.Leave += (s, e) => { ShowValue(); Committed?.Invoke(this, EventArgs.Empty); };
            _box.KeyDown += OnBoxKeyDown;
            _box.KeyPress += (s, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            _box.MouseWheel += (s, e) => Wheel(e);

            _repeat.Tick += (s, e) =>
            {
                _repeat.Interval = RepeatEveryMs;
                Step(_pressedArrow);
            };

            OnStateChanged();
            LayoutBox();
            ShowValue();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => _minimum;
            set { _minimum = value; Value = _value; }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _maximum;
            set { _maximum = value; Value = _value; }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                value = Math.Clamp(value, _minimum, _maximum);
                if (value == _value) { ShowValue(); return; }
                _value = value;
                ShowValue();
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Единица измерения справа от числа: «ч», «мин», «с»</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Suffix
        {
            get => _suffix;
            set { _suffix = value ?? ""; LayoutBox(); Invalidate(); }
        }

        protected override bool FocusedInside => _box.Focused;

        private int ArrowWidth => Ui.Px(22);

        private int SuffixWidth => _suffix.Length == 0 ? 0
            : TextRenderer.MeasureText(_suffix, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + Ui.Px(6);

        private void ShowValue()
        {
            _updatingText = true;
            try
            {
                string text = _value.ToString("00", CultureInfo.InvariantCulture);
                if (_box.Text != text) _box.Text = text;
            }
            finally
            {
                _updatingText = false;
            }
        }

        // Ввод цифрами: число применяется сразу, пока оно в границах; пустое поле и лишнее исправляются, когда поле покидают
        private void OnBoxTextChanged()
        {
            if (_updatingText) return;
            if (!int.TryParse(_box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int typed)) return;

            typed = Math.Clamp(typed, _minimum, _maximum);
            if (typed == _value) return;
            _value = typed;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnBoxKeyDown(object? sender, KeyEventArgs e)
        {
            int direction = e.KeyCode == Keys.Up ? 1 : e.KeyCode == Keys.Down ? -1 : 0;
            if (direction == 0) return;
            Step(direction);
            e.Handled = e.SuppressKeyPress = true;
        }

        private void Wheel(MouseEventArgs e)
        {
            // колёсико над полем меняет число, а не прокручивает список правил
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            Step(e.Delta > 0 ? 1 : -1);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Wheel(e);
            base.OnMouseWheel(e);
        }

        private void Step(int direction)
        {
            if (direction == 0 || !Enabled) return;
            Value = _value + direction;
            Committed?.Invoke(this, EventArgs.Empty);
        }

        private Rectangle UpArrow => new Rectangle(Width - ArrowWidth - 1, 1, ArrowWidth, Height / 2 - 1);
        private Rectangle DownArrow => new Rectangle(Width - ArrowWidth - 1, Height / 2, ArrowWidth, Height - Height / 2 - 1);

        private int ArrowAt(Point p) => UpArrow.Contains(p) ? 1 : DownArrow.Contains(p) ? -1 : 0;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int arrow = ArrowAt(e.Location);
            if (arrow != _hotArrow) { _hotArrow = arrow; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hotArrow = 0;
            StopRepeat();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int arrow = e.Button == MouseButtons.Left ? ArrowAt(e.Location) : 0;
            if (arrow != 0)
            {
                _pressedArrow = arrow;
                Step(arrow);
                _repeat.Interval = RepeatDelayMs;
                _repeat.Start();
            }
            else
            {
                _box.Focus();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            StopRepeat();
            base.OnMouseUp(e);
        }

        private void StopRepeat()
        {
            _repeat.Stop();
            _pressedArrow = 0;
        }

        protected override void OnStateChanged()
        {
            // вызывается и из конструктора базового класса, когда поле ещё не создано
            if (_box is null) return;

            _box.BackColor = FillColor;
            _box.ForeColor = Enabled ? ObsTheme.Text : ObsTheme.TextMuted;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            if (_suffix.Length > 0)
            {
                using var small = Ui.Font(scale: 0.92f);
                var rect = new Rectangle(0, 0, Width - ArrowWidth - Ui.Px(6), Height);
                TextRenderer.DrawText(g, _suffix, small, rect, ObsTheme.TextMuted,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }

            DrawArrow(g, UpArrow, up: true, hot: _hotArrow == 1);
            DrawArrow(g, DownArrow, up: false, hot: _hotArrow == -1);
        }

        private void DrawArrow(Graphics g, Rectangle area, bool up, bool hot)
        {
            if (!Enabled) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hot)
            {
                using var back = new SolidBrush(_pressedArrow != 0 ? ObsTheme.ButtonBgDown : ObsTheme.ButtonBgHover);
                g.FillRectangle(back, area);
            }

            float cx = area.X + area.Width / 2f, cy = area.Y + area.Height / 2f;
            float w = Ui.Px(4), h = Ui.Px(2.5f);
            var points = up
                ? new[] { new PointF(cx - w, cy + h), new PointF(cx + w, cy + h), new PointF(cx, cy - h) }
                : new[] { new PointF(cx - w, cy - h), new PointF(cx + w, cy - h), new PointF(cx, cy + h) };
            using var brush = new SolidBrush(hot ? ObsTheme.Text : ObsTheme.TextMuted);
            g.FillPolygon(brush, points);
        }

        private void LayoutBox()
        {
            // вызывается из конструктора базового класса, когда поле ещё не создано
            if (_box is null) return;

            int right = ArrowWidth + SuffixWidth + Ui.Px(6);
            _box.SetBounds(Ui.Px(6), Math.Max(0, (Height - _box.Height) / 2), Math.Max(10, Width - Ui.Px(6) - right), _box.Height);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            // вызывается и из конструктора базового класса, когда поле ещё не создано
            if (_box is null) return;
            _box.Font = Font;
            LayoutBox();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _repeat.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Галочка с подписью справа (как QCheckBox в OBS): квадрат со скруглением, внутри птичка</summary>
    internal sealed class ThemedCheckBox : Control
    {
        private bool _checked;
        private bool _hover;

        public event EventHandler? CheckedChanged;

        public ThemedCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
            Font = Ui.Font();
            Size = new Size(Ui.Px(200), Ui.Px(26));
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            int size = Ui.Px(20);
            var box = new RectangleF(0.5f, (Height - size) / 2f, size - 1, size - 1);
            bool ring = Focused && ShowFocusCues;
            Color fill = _checked ? (_hover ? ObsTheme.PrimaryLight : ObsTheme.Primary) : (_hover ? ObsTheme.InputBgHover : ObsTheme.InputBg);
            Color border = ring ? ObsTheme.FocusRing : _checked ? fill : _hover ? ObsTheme.InputBorderHover : ObsTheme.InputBg;

            using (var path = Ui.RoundedRect(box, Ui.Px(4)))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(border, ring ? 2f : 1f))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            if (_checked)
            {
                // на светлой теме светлая заливка требует тёмной птички
                using var tick = new Pen(ObsTheme.IsDark ? Color.White : Color.FromArgb(0x1D, 0x1F, 0x26), Math.Max(2f, Ui.Px(2)))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                float x = box.X, y = box.Y, s = box.Width;
                g.DrawLines(tick, new[]
                {
                    new PointF(x + s * 0.24f, y + s * 0.52f),
                    new PointF(x + s * 0.43f, y + s * 0.71f),
                    new PointF(x + s * 0.77f, y + s * 0.30f)
                });
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(size + Ui.Px(10), 0, Math.Max(0, Width - size - Ui.Px(10)), Height),
                Enabled ? ObsTheme.Text : ObsTheme.TextMuted,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    }

    /// <summary>Значок предупреждения: треугольник с восклицательным знаком</summary>
    internal sealed class WarningIcon : Control
    {
        public WarningIcon()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            TabStop = false;
            Size = new Size(Ui.Px(26), Ui.Px(26));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? ObsTheme.BaseBg);

            float pad = Ui.Px(2);
            var top = new PointF(Width / 2f, pad);
            var left = new PointF(pad, Height - pad - 1);
            var right = new PointF(Width - pad, Height - pad - 1);
            using (var brush = new SolidBrush(ObsTheme.WarnAccent))
            using (var pen = new Pen(ObsTheme.WarnAccent, Ui.Px(2)) { LineJoin = LineJoin.Round })
            {
                var triangle = new[] { top, right, left };
                g.FillPolygon(brush, triangle);
                g.DrawPolygon(pen, triangle);
            }

            using var bold = Ui.Font(FontStyle.Bold, 0.85f);
            TextRenderer.DrawText(g, "!", bold, new Rectangle(0, Ui.Px(3), Width, Height - Ui.Px(3)), ObsTheme.IsDark ? Color.FromArgb(0x1D, 0x1F, 0x26) : Color.White,
                TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}

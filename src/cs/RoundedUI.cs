using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace RemoteControl
{
    // 圆角工具：共享 GraphicsPath helper；并提供两个自绘控件。
    // 边框采用"两段填充"法：先画外层（border 色），再画内层（bg 色、inset 1px），
    // 两者之差自然形成 1px 边框，曲线段也连贯、无 AA 笔刷麻点。
    internal static class RoundedUI
    {
        // 全局开关：是否启用圆角界面。默认 false（直角），
        // 用户可在「设置 → 实验性功能」开启；程序启动时由 Program.Main 同步为已保存的设置。
        public static bool UseRoundedCorners { get; set; } = false;

        public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int r = System.Math.Min(radius, System.Math.Min(bounds.Width, bounds.Height) / 2);
            if (r <= 0) r = 0;
            var gp = new GraphicsPath();
            if (r == 0) { gp.AddRectangle(bounds); gp.CloseFigure(); return gp; }
            int d = r * 2;
            gp.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            gp.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            gp.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            gp.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        // 给任意 Control 套圆角 Region 裁剪（旧 API，保留）。
        // 关闭圆角时改为还原为矩形（Region=null），让开关即时生效。
        public static void Apply(Control c, int radius)
        {
            try
            {
                if (c == null) return;
                if (!UseRoundedCorners) { c.Region = null; return; }
                if (c.Width <= 1 || c.Height <= 1) return;
                int r = System.Math.Min(radius, System.Math.Min(c.Width, c.Height) / 2);
                if (r <= 0) { c.Region = null; return; }
                using var gp = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), r);
                c.Region = new Region(gp);
            }
            catch { }
        }

        // 遍历整棵控件树，按当前 UseRoundedCorners 重新套/还原圆角。
        // 只处理自绘控件（RoundedPanel / RoundedButton）：
        //   - RoundedPanel 只需重绘（OnPaint 读取全局开关）
        //   - RoundedButton 需重设 Region（还原/裁剪）并重绘
        // 普通 Panel/Button 不再在此处理（MainForm 里已无需要圆角的普通控件，
        // 避免把工具栏容器 top/_statusPanel 也裁成圆角，改变既有外观）。
        // FlowLayoutPanel 不裁剪（避免把里面按钮角切掉），但仍递归其子控件。
        public static void ApplyStyle(Control root)
        {
            if (root == null) return;
            ApplyStyleRecursive(root);
        }
        private static void ApplyStyleRecursive(Control c)
        {
            if (c is RoundedPanel rp) rp.Invalidate();
            else if (c is RoundedButton rb) rb.RefreshStyle();
            foreach (Control child in c.Controls) ApplyStyleRecursive(child);
        }
    }

    // 自绘圆角 Panel：两段填充法画"背景 + 1px 圆角边框"，无 AA 笔刷麻点。
    internal class RoundedPanel : Panel
    {
        public int CornerRadius { get; set; } = 12;
        public Color BorderColor { get; set; } = Color.FromArgb(0xCF, 0xCF, 0xCF);
        public float BorderWidth { get; set; } = 1f;

        public RoundedPanel()
        {
            BorderStyle = BorderStyle.None;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                if (Width <= 1 || Height <= 1) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                int radius = RoundedUI.UseRoundedCorners ? CornerRadius : 0;
                int inset = (int)Math.Ceiling(BorderWidth);
                var outerBounds = new Rectangle(0, 0, Width, Height);
                var innerBounds = new Rectangle(inset, inset, Width - 2 * inset, Height - 2 * inset);
                int innerRadius = Math.Max(0, radius - inset);

                // 外层：border 色（充作边框）
                using (var outerPath = RoundedUI.RoundedRect(outerBounds, radius))
                using (var borderBrush = new SolidBrush(BorderColor))
                    g.FillPath(borderBrush, outerPath);

                // 内层：bg 色，盖在外层上面 → 中间露出 1px border 色
                using (var innerPath = RoundedUI.RoundedRect(innerBounds, innerRadius))
                using (var bg = new SolidBrush(BackColor))
                    g.FillPath(bg, innerPath);
            }
            catch { }
            base.OnPaint(e);
        }
    }

    // 自绘圆角 Button：两段填充法 + 状态色 + 文字。支持 hover/press/disabled。
    internal class RoundedButton : Button
    {
        public int CornerRadius { get; set; } = 8;
        public Color BorderColor { get; set; } = Color.FromArgb(0xB0, 0xB6, 0xBE);
        public float BorderWidth { get; set; } = 1f;
        public Color FaceColor { get; set; } = Color.FromArgb(0xEA, 0xED, 0xF0); // 按钮面：原版那种浅灰立体面
        public Color HoverBackColor { get; set; } = Color.FromArgb(0xEC, 0xF1, 0xF8);
        public Color PressedBackColor { get; set; } = Color.FromArgb(0xDD, 0xE6, 0xF2);
        public Color DisabledBackColor { get; set; } = Color.FromArgb(0xF5, 0xF4, 0xEE);
        public Color DisabledBorderColor { get; set; } = Color.FromArgb(0xE0, 0xDF, 0xD8);
        public Color DisabledForeColor { get; set; } = Color.FromArgb(0xA8, 0xA7, 0xA1);

        private bool _hover, _pressed;

        // 把颜色整体压暗 amt(0~1)。供圆角模式下的"上亮下暗"渐变使用。
        private static Color Darken(Color c, float amt)
        {
            float f = 1f - amt;
            return Color.FromArgb(c.A,
                (int)(c.R * f + 0.5f),
                (int)(c.G * f + 0.5f),
                (int)(c.B * f + 0.5f));
        }

        public RoundedButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            BackColor = Color.White;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            UpdateRegion();
        }

        // 关掉系统焦点指示器（那圈虚线框被 Region 裁切后会变成零星小点/小段，
        // 视觉上像"按钮角落有奇怪色块"）。按钮仍可获焦、键盘 Enter/Space 仍触发。
        protected override bool ShowFocusCues => false;

        protected override void OnResize(EventArgs e) { UpdateRegion(); base.OnResize(e); }
        protected override void OnFontChanged(EventArgs e) { UpdateRegion(); base.OnFontChanged(e); }
        protected override void OnTextChanged(EventArgs e) { UpdateRegion(); base.OnTextChanged(e); }

        private void UpdateRegion()
        {
            try
            {
                // 直角：还原矩形（无裁剪，4 角可点）。
                if (!RoundedUI.UseRoundedCorners) { Region = null; return; }
                if (Width <= 1 || Height <= 1) { Region = null; return; }
                // 圆角：Region 二进制裁掉矩形 4 角（防止按钮矩形角外漏出深色背景，
                // 即"4 角黑边"老问题）。曲线本身在 OnPaint 中用 AA FillPath 画，
                // 像素级平滑，无阶梯。
                using var gp = RoundedUI.RoundedRect(new Rectangle(0, 0, Width, Height), CornerRadius);
                Region = new Region(gp);
            }
            catch { Region = null; }
        }

        // 开关切换后调用：重设 Region（裁剪/还原）并重绘，使直角↔圆角即时生效。
        public void RefreshStyle()
        {
            UpdateRegion();
            try { Invalidate(); } catch { }
        }

        // 不让 Button 默认 OnPaintBackground 用系统色重填整个矩形；改用父背景色覆盖
        // 整个矩形（4 角区域也覆盖到），即使 Region 没及时更新也不会出现黑边闪。
        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            try
            {
                var bg = Parent?.BackColor ?? SystemColors.Control;
                using var b = new SolidBrush(bg);
                pevent.Graphics.FillRectangle(b, ClientRectangle);
            }
            catch { base.OnPaintBackground(pevent); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e)   { if (_pressed)  { _pressed = false; Invalidate(); } base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                if (Width <= 1 || Height <= 1) return;
                var g = e.Graphics;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

                bool dis = !Enabled;
                Color border = dis ? DisabledBorderColor : BorderColor;
                Color fg     = dis ? DisabledForeColor   : ForeColor;

                bool useRounded = RoundedUI.UseRoundedCorners;
                int inset = (int)Math.Ceiling(BorderWidth);
                var outerBounds = new Rectangle(0, 0, Width, Height);
                var innerBounds = new Rectangle(inset, inset, Math.Max(0, Width - 2 * inset), Math.Max(0, Height - 2 * inset));
                int radius = useRounded ? CornerRadius : 0;
                int innerRadius = Math.Max(0, radius - inset);

                Color face = dis      ? DisabledBackColor
                           : _pressed ? PressedBackColor
                           : _hover   ? HoverBackColor
                                      : FaceColor;

                if (useRounded)
                {
                    // 圆角：单一 AA FillPath 源 → 曲线像素级平滑，无阶梯方块。
                    var prev = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var outerPath = RoundedUI.RoundedRect(outerBounds, radius))
                    using (var innerPath = RoundedUI.RoundedRect(innerBounds, innerRadius))
                    using (var borderBrush = new SolidBrush(border))
                    using (var faceBrush = new SolidBrush(face))
                    {
                        g.FillPath(borderBrush, outerPath);
                        g.FillPath(faceBrush, innerPath);

                        // 上亮下暗竖向渐变
                        Color top, bot;
                        if (dis) { top = bot = face; }
                        else if (_pressed) { top = Darken(face, 0.08f); bot = face; }
                        else if (_hover)   { top = face; bot = Darken(face, 0.04f); }
                        else               { top = face; bot = Darken(face, 0.07f); }
                        if (top != bot && innerBounds.Width > 0 && innerBounds.Height > 0)
                        {
                            using var grad = new LinearGradientBrush(innerBounds, top, bot, LinearGradientMode.Vertical);
                            g.FillPath(grad, innerPath);
                        }
                    }
                    g.SmoothingMode = prev;
                }
                else
                {
                    // 直角：FillRectangle 直接画，4 角为矩形无曲线 AA 锯齿。
                    using (var borderBrush = new SolidBrush(border))
                        g.FillRectangle(borderBrush, outerBounds);
                    using (var faceBrush = new SolidBrush(face))
                        g.FillRectangle(faceBrush, innerBounds);

                    // 经典 1px 凸起斜面（左上高光、右下阴影），pressed 反转。
                    Color hi = Color.White;
                    Color sh = dis ? Color.FromArgb(0xCF, 0xCF, 0xCF) : Color.FromArgb(0x9A, 0xA0, 0xA8);
                    bool pressedIn = _pressed && !dis;
                    Color topLeft  = pressedIn ? sh : hi;
                    Color botRight = pressedIn ? hi : sh;

                    if (innerBounds.Width > 0 && innerBounds.Height > 0)
                    {
                        using (var hiBrush = new SolidBrush(topLeft))
                        using (var shBrush = new SolidBrush(botRight))
                        {
                            g.FillRectangle(hiBrush, innerBounds.X, innerBounds.Y, innerBounds.Width, 1);
                            g.FillRectangle(hiBrush, innerBounds.X, innerBounds.Y, 1, innerBounds.Height);
                            g.FillRectangle(shBrush, innerBounds.X, innerBounds.Bottom - 1, innerBounds.Width, 1);
                            g.FillRectangle(shBrush, innerBounds.Right - 1, innerBounds.Y, 1, innerBounds.Height);
                        }
                    }
                }

                // 文字（AA 抗锯齿）
                var prevAA = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var textBounds = new Rectangle(0, 0, Width, Height);
                TextRenderer.DrawText(g, Text, Font, textBounds, fg,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix);
                g.SmoothingMode = prevAA;
            }
            catch { }
        }
    }
}

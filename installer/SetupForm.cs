using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace WmaoRuInstaller
{
    internal sealed class SetupForm : Form
    {
        private const string SupportUrl = "https://github.com/neoru-droid/we-march-as-one-ru#support";

        private static readonly Color BgDark = Color.FromArgb(0x23, 0x21, 0x1C);
        private static readonly Color CardBg = Color.FromArgb(0x33, 0x35, 0x3D);
        private static readonly Color CardBorder = Color.FromArgb(0x46, 0x48, 0x52);
        private static readonly Color InputBg = Color.FromArgb(0x1B, 0x1A, 0x16);
        private static readonly Color TextPrimary = Color.FromArgb(0xE8, 0xE6, 0xE1);
        private static readonly Color TextMuted = Color.FromArgb(0xA6, 0xA8, 0xB3);
        private static readonly Color PrimaryBtnFill = Color.FromArgb(0xE8, 0xE6, 0xE1);
        private static readonly Color PrimaryBtnHover = Color.FromArgb(0xF5, 0xF4, 0xF0);
        private static readonly Color PrimaryBtnLip = Color.FromArgb(0xB8, 0xB4, 0xA8);
        private static readonly Color PrimaryBtnText = Color.FromArgb(0x23, 0x21, 0x1C);
        private static readonly Color SecondaryBtnFill = Color.FromArgb(0x46, 0x48, 0x52);
        private static readonly Color SecondaryBtnHover = Color.FromArgb(0x55, 0x58, 0x64);
        private static readonly Color SecondaryBtnLip = Color.FromArgb(0x29, 0x2B, 0x31);
        private static readonly Color AccentRed = Color.FromArgb(0xE0, 0x46, 0x3A);
        private static readonly Color AccentBlue = Color.FromArgb(0x3B, 0x82, 0xF6);
        private static readonly Color AccentGold = Color.FromArgb(0xD8, 0xB4, 0x5A);
        private static readonly Color StatusOk = Color.FromArgb(0x8F, 0xBF, 0x7A);
        private static readonly Color StatusWarn = Color.FromArgb(0xE0, 0x7A, 0x68);

        private readonly string _appBaseDir;
        private readonly float _scale;

        private Font _fontHeaderTitle;
        private Font _fontHeaderSub;
        private Font _fontSection;
        private Font _fontBody;
        private Font _fontBodyBold;
        private Font _fontSmall;
        private Font _fontPrimaryBtn;
        private Font _fontSecondaryBtn;
        private Font _fontLog;
        private Icon _generatedIcon;

        private TextBox _pathBox;
        private TabletopButton _browseBtn;
        private TabletopCheckRow _achCheck;
        private TabletopButton _installBtn;
        private TabletopButton _restoreBtn;
        private TextBox _logBox;
        private TabletopButton _supportBtn;

        private string _pathBadgeText = "";
        private Color _pathBadgeColor = TextMuted;
        private bool _isBusy = false;

        private Rectangle _headerCardRect;
        private Rectangle _pathCardRect;
        private Rectangle _optionsCardRect;
        private Rectangle _logCardRect;
        private Rectangle _pathInputBorderRect;

        public SetupForm(string appBaseDir)
        {
            _appBaseDir = appBaseDir;
            _scale = DetectDpiScale();

            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgDark;
            ForeColor = TextPrimary;
            Text = "We March as One — Русификатор v1.0";
            ClientSize = new Size(S(696), S(564));

            _generatedIcon = BuildTacticalIcon();
            if (_generatedIcon != null)
            {
                Icon = _generatedIcon;
            }

            InitFonts();
            BuildLayout();
            InitializePathAndLog();
        }

        private int S(int px)
        {
            return (int)Math.Round(px * _scale);
        }

        private static float DetectDpiScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    if (g.DpiX > 0f)
                    {
                        float s = g.DpiX / 96f;
                        if (s < 1f) return 1f;
                        if (s > 3f) return 3f;
                        return s;
                    }
                }
            }
            catch
            {
            }
            return 1f;
        }

        private void InitFonts()
        {
            _fontHeaderTitle = new Font("Segoe UI", S(20), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontHeaderSub = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontSection = new Font("Segoe UI", S(11), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontBody = new Font("Segoe UI", S(13), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontBodyBold = new Font("Segoe UI", S(13), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontSmall = new Font("Segoe UI", S(11), FontStyle.Regular, GraphicsUnit.Pixel);
            _fontPrimaryBtn = new Font("Segoe UI", S(14), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontSecondaryBtn = new Font("Segoe UI", S(13), FontStyle.Bold, GraphicsUnit.Pixel);
            _fontLog = new Font("Consolas", S(12), FontStyle.Regular, GraphicsUnit.Pixel);
        }

        private void BuildLayout()
        {
            int pad = S(20);
            int contentW = ClientSize.Width - pad * 2;

            _headerCardRect = new Rectangle(pad, S(18), contentW, S(78));
            _pathCardRect = new Rectangle(pad, S(108), contentW, S(88));
            _optionsCardRect = new Rectangle(pad, S(208), contentW, S(64));

            int browseW = S(116);
            int inputX = _pathCardRect.X + S(16);
            int inputY = _pathCardRect.Y + S(38);
            int inputH = S(34);
            int inputW = _pathCardRect.Width - S(32) - browseW - S(10);

            _pathInputBorderRect = new Rectangle(inputX, inputY, inputW, inputH);

            _pathBox = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = InputBg,
                ForeColor = TextPrimary,
                Font = _fontBody,
                Location = new Point(inputX + S(10), inputY + S(8)),
                Width = inputW - S(20)
            };
            _pathBox.TextChanged += delegate { RefreshPathStatus(); };
            Controls.Add(_pathBox);

            _browseBtn = new TabletopButton
            {
                Text = "Обзор...",
                Font = _fontSecondaryBtn,
                IsPrimary = false,
                ScaleFactor = _scale,
                Bounds = new Rectangle(inputX + inputW + S(10), inputY, browseW, inputH)
            };
            _browseBtn.Click += OnBrowseClicked;
            Controls.Add(_browseBtn);

            _achCheck = new TabletopCheckRow
            {
                Checked = true,
                TitleText = "Добавить экран «Достижения» в главное меню игры",
                SubtitleText = "Встраивает русскую панель 30 достижений со статусом из Steam",
                TitleFont = _fontBodyBold,
                SubtitleFont = _fontSmall,
                ScaleFactor = _scale,
                Bounds = new Rectangle(
                    _optionsCardRect.X + S(14),
                    _optionsCardRect.Y + S(8),
                    _optionsCardRect.Width - S(28),
                    _optionsCardRect.Height - S(16))
            };
            Controls.Add(_achCheck);

            int btnY = S(286);
            int btnH = S(48);
            int gap = S(12);
            int primaryW = (int)(contentW * 0.60f);
            int secondaryW = contentW - primaryW - gap;

            _installBtn = new TabletopButton
            {
                Text = "УСТАНОВИТЬ РУСИФИКАТОР",
                Font = _fontPrimaryBtn,
                IsPrimary = true,
                ScaleFactor = _scale,
                Bounds = new Rectangle(pad, btnY, primaryW, btnH)
            };
            _installBtn.Click += OnInstallClicked;
            Controls.Add(_installBtn);

            _restoreBtn = new TabletopButton
            {
                Text = "ВЕРНУТЬ ОРИГИНАЛ",
                Font = _fontSecondaryBtn,
                IsPrimary = false,
                ScaleFactor = _scale,
                Bounds = new Rectangle(pad + primaryW + gap, btnY, secondaryW, btnH)
            };
            _restoreBtn.Click += OnRestoreClicked;
            Controls.Add(_restoreBtn);

            _logCardRect = new Rectangle(pad, S(348), contentW, S(160));
            _logBox = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                ScrollBars = ScrollBars.Vertical,
                BackColor = InputBg,
                ForeColor = Color.FromArgb(0xD6, 0xD4, 0xCD),
                Font = _fontLog,
                Location = new Point(_logCardRect.X + S(12), _logCardRect.Y + S(30)),
                Size = new Size(_logCardRect.Width - S(24), _logCardRect.Height - S(40))
            };
            Controls.Add(_logBox);

            int supportW = S(254);
            int supportH = S(30);
            _supportBtn = new TabletopButton
            {
                Text = "☕ Поддержать автора перевода",
                Font = _fontSmall,
                IsPrimary = false,
                ScaleFactor = _scale,
                Bounds = new Rectangle(ClientSize.Width - pad - supportW, S(520), supportW, supportH)
            };
            _supportBtn.Click += OnSupportClicked;
            Controls.Add(_supportBtn);
        }

        private void InitializePathAndLog()
        {
            string detected = SteamLocator.TryAutoDetectGameDirectory(_appBaseDir);
            if (!string.IsNullOrEmpty(detected))
            {
                _pathBox.Text = detected;
                AppendLog("Папка игры найдена автоматически: " + detected);
                if (BackupManager.HasBackup(detected))
                {
                    BackupManifest mf = BackupManager.ReadManifestOrEmpty(detected);
                    _achCheck.Checked = mf.AchievementsUi;
                    AppendLog("Статус: русификатор уже установлен. Доступно переустановление или возврат оригинала.");
                }
                else
                {
                    AppendLog("Статус: готов к установке полной русской локализации (текст, 5 шрифтов, достижения).");
                }
            }
            else
            {
                RefreshPathStatus();
                AppendLog("Автопоиск не нашёл установленную игру. Нажмите «Обзор...» и укажите папку We March as One.");
            }
            _logBox.SelectionStart = 0;
            _logBox.SelectionLength = 0;
        }

        private void RefreshPathStatus()
        {
            string current = _pathBox.Text.Trim();
            if (SteamLocator.IsValidGameDirectory(current))
            {
                if (BackupManager.HasBackup(current))
                {
                    _pathBadgeText = "✓ Установлен русификатор (резервная копия сохранена)";
                    _pathBadgeColor = AccentGold;
                }
                else
                {
                    _pathBadgeText = "✓ Файлы игры найдены";
                    _pathBadgeColor = StatusOk;
                }
            }
            else if (string.IsNullOrEmpty(current))
            {
                _pathBadgeText = "Укажите папку с игрой";
                _pathBadgeColor = StatusWarn;
            }
            else
            {
                _pathBadgeText = "WeMarchAsOne_Data\\resources.assets не найден";
                _pathBadgeColor = StatusWarn;
            }
            Invalidate(_pathCardRect);
        }

        private void OnBrowseClicked(object sender, EventArgs e)
        {
            if (_isBusy) return;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Выберите папку установленной игры We March as One";
                dlg.ShowNewFolderButton = false;
                if (SteamLocator.SafeDirectoryExists(_pathBox.Text))
                {
                    dlg.SelectedPath = _pathBox.Text;
                }
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                {
                    _pathBox.Text = dlg.SelectedPath;
                }
            }
        }

        private void OnInstallClicked(object sender, EventArgs e)
        {
            if (_isBusy) return;
            string selectedDir = _pathBox.Text.Trim();
            bool enableAch = _achCheck.Checked;

            SetBusy(true);
            _logBox.Clear();
            AppendLog("Запуск установки русификатора v1.0...");

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    InstallOptions opts = new InstallOptions
                    {
                        GameDir = selectedDir,
                        EnableAchievementsUi = enableAch
                    };
                    InstallerEngine.Install(_appBaseDir, opts, AppendLogThreadSafe);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        RefreshPathStatus();
                        SetBusy(false);
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        AppendLog("ОШИБКА: " + ex.Message);
                        RefreshPathStatus();
                        SetBusy(false);
                    });
                }
            });
        }

        private void OnRestoreClicked(object sender, EventArgs e)
        {
            if (_isBusy) return;
            string selectedDir = _pathBox.Text.Trim();

            SetBusy(true);
            _logBox.Clear();
            AppendLog("Запуск возврата оригинальных файлов...");

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    InstallerEngine.Restore(_appBaseDir, selectedDir, null, AppendLogThreadSafe);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        RefreshPathStatus();
                        SetBusy(false);
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        AppendLog("ОШИБКА: " + ex.Message);
                        RefreshPathStatus();
                        SetBusy(false);
                    });
                }
            });
        }

        private void OnSupportClicked(object sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(SupportUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog("Не удалось открыть браузер: " + ex.Message);
            }
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            _installBtn.Enabled = !busy;
            _restoreBtn.Enabled = !busy;
            _browseBtn.Enabled = !busy;
            _pathBox.ReadOnly = busy;
            _achCheck.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void AppendLogThreadSafe(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke((MethodInvoker)delegate { AppendLog(line); });
                }
                catch
                {
                }
            }
            else
            {
                AppendLog(line);
            }
        }

        private void AppendLog(string line)
        {
            if (_logBox.TextLength > 0)
            {
                _logBox.AppendText(Environment.NewLine);
            }
            _logBox.AppendText(line);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            DrawCard(g, _headerCardRect);
            DrawHeaderContent(g, _headerCardRect);

            DrawCard(g, _pathCardRect);
            DrawPathCardLabels(g, _pathCardRect);

            using (SolidBrush ib = new SolidBrush(InputBg))
            using (Pen ip = new Pen(CardBorder, 1f))
            {
                FillRoundedRect(g, ib, _pathInputBorderRect, S(6));
                DrawRoundedRect(g, ip, _pathInputBorderRect, S(6));
            }

            DrawCard(g, _optionsCardRect);

            DrawCard(g, _logCardRect);
            TextRenderer.DrawText(
                g,
                "ЖУРНАЛ УСТАНОВКИ",
                _fontSection,
                new Point(_logCardRect.X + S(14), _logCardRect.Y + S(9)),
                TextMuted);

            TextRenderer.DrawText(
                g,
                "Steam AppID 5189160  ·  SIL OFL 1.1 + MIT",
                _fontSmall,
                new Point(S(22), S(527)),
                TextMuted);
        }

        private void DrawHeaderContent(Graphics g, Rectangle r)
        {
            // Draw tabletop tactical formation emblem on the left
            int emblemX = r.X + S(18);
            int emblemY = r.Y + S(16);
            int emblemSize = S(46);
            Rectangle badgeRect = new Rectangle(emblemX, emblemY, emblemSize, emblemSize);
            using (SolidBrush bg = new SolidBrush(InputBg))
            using (Pen border = new Pen(CardBorder, 1f))
            {
                FillRoundedRect(g, bg, badgeRect, S(8));
                DrawRoundedRect(g, border, badgeRect, S(8));
            }

            DrawTriangle(g, AccentRed, emblemX + S(15), emblemY + S(14), S(11), true);
            DrawTriangle(g, AccentRed, emblemX + S(31), emblemY + S(14), S(11), true);
            DrawTriangle(g, AccentBlue, emblemX + S(15), emblemY + S(32), S(11), false);
            DrawTriangle(g, AccentBlue, emblemX + S(31), emblemY + S(32), S(11), false);

            int textX = emblemX + emblemSize + S(14);
            TextRenderer.DrawText(
                g,
                "WE MARCH AS ONE — РУСИФИКАТОР v1.0",
                _fontHeaderTitle,
                new Point(textX, r.Y + S(14)),
                TextPrimary);

            TextRenderer.DrawText(
                g,
                "Полный перевод всего текста игры, шрифты 1:1 и экран достижений  ·  автор: neoru-droid",
                _fontHeaderSub,
                new Point(textX + S(1), r.Y + S(44)),
                TextMuted);
        }

        private void DrawPathCardLabels(Graphics g, Rectangle r)
        {
            TextRenderer.DrawText(
                g,
                "ПАПКА С ИГРОЙ (STEAM)",
                _fontSection,
                new Point(r.X + S(16), r.Y + S(12)),
                TextMuted);

            if (!string.IsNullOrEmpty(_pathBadgeText))
            {
                Size badgeSize = TextRenderer.MeasureText(_pathBadgeText, _fontSmall);
                TextRenderer.DrawText(
                    g,
                    _pathBadgeText,
                    _fontSmall,
                    new Point(r.Right - S(16) - badgeSize.Width, r.Y + S(12)),
                    _pathBadgeColor);
            }
        }

        private void DrawCard(Graphics g, Rectangle r)
        {
            using (SolidBrush b = new SolidBrush(CardBg))
            using (Pen p = new Pen(CardBorder, 1f))
            {
                FillRoundedRect(g, b, r, S(8));
                DrawRoundedRect(g, p, r, S(8));
            }
        }

        private static void DrawTriangle(Graphics g, Color c, int cx, int cy, int size, bool pointDown)
        {
            float half = size * 0.5f;
            PointF[] pts = pointDown
                ? new PointF[]
                {
                    new PointF(cx - half, cy - half * 0.75f),
                    new PointF(cx + half, cy - half * 0.75f),
                    new PointF(cx, cy + half * 0.85f)
                }
                : new PointF[]
                {
                    new PointF(cx, cy - half * 0.85f),
                    new PointF(cx + half, cy + half * 0.75f),
                    new PointF(cx - half, cy + half * 0.75f)
                };
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPolygon(b, pts);
            }
        }

        private Icon BuildTacticalIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(48, 48))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(BgDark))
                    using (Pen pen = new Pen(PrimaryBtnFill, 2f))
                    {
                        FillRoundedRect(g, bg, new Rectangle(2, 2, 44, 44), 9);
                        DrawRoundedRect(g, pen, new Rectangle(2, 2, 44, 44), 9);
                    }
                    DrawTriangle(g, AccentRed, 16, 15, 11, true);
                    DrawTriangle(g, AccentRed, 32, 15, 11, true);
                    DrawTriangle(g, AccentBlue, 16, 33, 11, false);
                    DrawTriangle(g, AccentBlue, 32, 33, 11, false);
                    IntPtr hIcon = bmp.GetHicon();
                    return Icon.FromHandle(hIcon);
                }
            }
            catch
            {
                return null;
            }
        }

        internal static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
        {
            using (GraphicsPath path = CreateRoundedPath(rect, radius))
            {
                g.FillPath(brush, path);
            }
        }

        internal static void DrawRoundedRect(Graphics g, Pen pen, Rectangle rect, int radius)
        {
            Rectangle adj = new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            using (GraphicsPath path = CreateRoundedPath(adj, radius))
            {
                g.DrawPath(pen, path);
            }
        }

        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_fontHeaderTitle != null) _fontHeaderTitle.Dispose();
                if (_fontHeaderSub != null) _fontHeaderSub.Dispose();
                if (_fontSection != null) _fontSection.Dispose();
                if (_fontBody != null) _fontBody.Dispose();
                if (_fontBodyBold != null) _fontBodyBold.Dispose();
                if (_fontSmall != null) _fontSmall.Dispose();
                if (_fontPrimaryBtn != null) _fontPrimaryBtn.Dispose();
                if (_fontSecondaryBtn != null) _fontSecondaryBtn.Dispose();
                if (_fontLog != null) _fontLog.Dispose();
                if (_generatedIcon != null) _generatedIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class TabletopButton : Control
        {
            public bool IsPrimary;
            public float ScaleFactor = 1f;
            private bool _hover;
            private bool _pressed;

            public TabletopButton()
            {
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw
                    | ControlStyles.UserPaint
                    | ControlStyles.Selectable,
                    true);
                Cursor = Cursors.Hand;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hover = false;
                _pressed = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button == MouseButtons.Left)
                {
                    _pressed = true;
                    Invalidate();
                }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                _pressed = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Parent != null ? Parent.BackColor : BgDark);

                int radius = Math.Max(5, (int)Math.Round(7 * ScaleFactor));
                int lipH = Math.Max(2, (int)Math.Round(3 * ScaleFactor));

                Color fill = IsPrimary
                    ? (_hover && Enabled ? PrimaryBtnHover : PrimaryBtnFill)
                    : (_hover && Enabled ? SecondaryBtnHover : SecondaryBtnFill);
                Color lip = IsPrimary ? PrimaryBtnLip : SecondaryBtnLip;
                Color fg = IsPrimary ? PrimaryBtnText : TextPrimary;

                if (!Enabled)
                {
                    fill = Color.FromArgb(0x3A, 0x3B, 0x42);
                    lip = Color.FromArgb(0x28, 0x29, 0x2E);
                    fg = TextMuted;
                }

                Rectangle lipRect = new Rectangle(0, lipH, Width, Height - lipH);
                int topY = (_pressed && Enabled) ? (lipH - 1) : 0;
                Rectangle faceRect = new Rectangle(0, topY, Width, Height - lipH);

                using (SolidBrush lb = new SolidBrush(lip))
                using (SolidBrush fb = new SolidBrush(fill))
                {
                    FillRoundedRect(g, lb, lipRect, radius);
                    FillRoundedRect(g, fb, faceRect, radius);
                }

                TextRenderer.DrawText(
                    g,
                    Text,
                    Font,
                    faceRect,
                    fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private sealed class TabletopCheckRow : Control
        {
            public bool Checked = true;
            public string TitleText = "";
            public string SubtitleText = "";
            public Font TitleFont;
            public Font SubtitleFont;
            public float ScaleFactor = 1f;
            private bool _hover;

            public TabletopCheckRow()
            {
                SetStyle(
                    ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw
                    | ControlStyles.UserPaint
                    | ControlStyles.Selectable,
                    true);
                Cursor = Cursors.Hand;
                BackColor = CardBg;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hover = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hover = false;
                Invalidate();
            }

            protected override void OnClick(EventArgs e)
            {
                base.OnClick(e);
                if (Enabled)
                {
                    Checked = !Checked;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(CardBg);

                int boxSize = (int)Math.Round(22 * ScaleFactor);
                int boxX = (int)Math.Round(4 * ScaleFactor);
                int boxY = (Height - boxSize) / 2;
                Rectangle boxRect = new Rectangle(boxX, boxY, boxSize, boxSize);
                int radius = Math.Max(4, (int)Math.Round(5 * ScaleFactor));

                Color fill = Checked ? PrimaryBtnFill : InputBg;
                Color border = (_hover && Enabled) ? PrimaryBtnFill : CardBorder;

                using (SolidBrush b = new SolidBrush(fill))
                using (Pen p = new Pen(border, Math.Max(1f, 1.5f * ScaleFactor)))
                {
                    FillRoundedRect(g, b, boxRect, radius);
                    if (!Checked)
                    {
                        DrawRoundedRect(g, p, boxRect, radius);
                    }
                }

                if (Checked)
                {
                    using (Pen checkPen = new Pen(PrimaryBtnText, Math.Max(2f, 2.4f * ScaleFactor)))
                    {
                        checkPen.StartCap = LineCap.Round;
                        checkPen.EndCap = LineCap.Round;
                        checkPen.LineJoin = LineJoin.Round;
                        PointF p1 = new PointF(boxRect.X + boxSize * 0.24f, boxRect.Y + boxSize * 0.52f);
                        PointF p2 = new PointF(boxRect.X + boxSize * 0.43f, boxRect.Y + boxSize * 0.72f);
                        PointF p3 = new PointF(boxRect.X + boxSize * 0.77f, boxRect.Y + boxSize * 0.30f);
                        g.DrawLines(checkPen, new PointF[] { p1, p2, p3 });
                    }
                }

                int textX = boxRect.Right + (int)Math.Round(12 * ScaleFactor);
                TextRenderer.DrawText(
                    g,
                    TitleText,
                    TitleFont ?? Font,
                    new Point(textX, (int)Math.Round(4 * ScaleFactor)),
                    Enabled ? TextPrimary : TextMuted);

                TextRenderer.DrawText(
                    g,
                    SubtitleText,
                    SubtitleFont ?? Font,
                    new Point(textX, (int)Math.Round(25 * ScaleFactor)),
                    TextMuted);
            }
        }
    }
}

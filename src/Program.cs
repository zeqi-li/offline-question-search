using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OfflineQuestions
{
    internal static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        public static void EnableDpi()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }
    }

    internal static class Program
    {
        public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        [STAThread]
        private static int Main(string[] args)
        {
            Native.EnableDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && args[0] == "--self-test")
                return Diagnostics.Run(args.Length > 1 ? args[1] : Path.Combine(Root, "verification", "report.json"));
            if (args.Length > 0 && args[0] == "--fixture") { Diagnostics.ShowFixture(); return 0; }
            if (args.Length > 0 && args[0] == "--idle-test") { Diagnostics.MeasureIdle(); return 0; }
            bool created;
            using (var mutex = new Mutex(true, "Local\\OfflineQuestionSearch_20261027", out created))
            {
                if (!created)
                {
                    var hwnd = Native.FindWindow(null, "离线搜题");
                    if (hwnd != IntPtr.Zero) { Native.ShowWindow(hwnd, 9); Native.SetForegroundWindow(hwnd); }
                    return 0;
                }
                Application.ThreadException += (s, e) => ReportError(e.Exception);
                Application.Run(new MainForm());
            }
            return 0;
        }
        public static void Log(string message)
        {
            try
            {
                string folder = Path.Combine(Root, "logs");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, "runtime.log");
                if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.WriteAllText(file, "", Encoding.UTF8);
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + message + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
        public static void ReportError(Exception error)
        {
            Log(error.ToString());
            MessageBox.Show(error.Message, "离线搜题 · 运行问题", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly Button capture = new Button(), stop = new Button();
        private readonly Task<QuestionBank> bank;
        private readonly Task<OcrService> ocr;
        private CancellationTokenSource current;
        private int generation;
        private AnswerForm answer;
        public MainForm()
        {
            Text = "离线搜题";
            Font = new Font("Microsoft YaHei UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(308, 82);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;
            BackColor = Color.FromArgb(246, 248, 251);
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 24, area.Top + 28);
            capture.Text = "截图搜题";
            capture.SetBounds(16, 17, 134, 46);
            capture.BackColor = Color.FromArgb(35, 100, 220);
            capture.ForeColor = Color.White;
            capture.FlatStyle = FlatStyle.Flat;
            capture.FlatAppearance.BorderSize = 0;
            stop.Text = "结束检测";
            stop.SetBounds(158, 17, 134, 46);
            stop.BackColor = Color.White;
            stop.FlatStyle = FlatStyle.Flat;
            stop.FlatAppearance.BorderColor = Color.FromArgb(215, 220, 230);
            Controls.AddRange(new Control[] { capture, stop });
            capture.Click += async (s, e) => await CaptureAsync();
            stop.Click += (s, e) => Stop();
            FormClosing += (s, e) => Stop();
            bank = Task.Run(() => QuestionBank.Load(Program.Root));
            ocr = Task.Run(() => new OcrService());
            Shown += async (s, e) =>
            {
                try
                {
                    var loaded = await bank;
                    await ocr;
                    Program.Log("ready questions=" + loaded.Questions.Count + " warnings=" + loaded.Warnings.Count);
                    foreach (var warning in loaded.Warnings) Program.Log(warning);
                }
                catch (Exception ex) { capture.Enabled = false; Program.ReportError(ex); }
            };
        }
        private void CloseAnswer()
        {
            if (answer != null) { answer.Close(); answer.Dispose(); answer = null; }
        }
        private void Stop()
        {
            generation++;
            if (current != null) { current.Cancel(); current = null; }
            capture.Enabled = true;
            CloseAnswer();
        }
        private async Task CaptureAsync()
        {
            Stop();
            int version = generation;
            var source = new CancellationTokenSource();
            current = source;
            var token = source.Token;
            capture.Enabled = false;
            Bitmap crop = null;
            Rectangle region = Rectangle.Empty;
            try
            {
                Hide();
                await Task.Delay(100, token);
                using (var selector = new SelectionForm())
                {
                    if (selector.ShowDialog() != DialogResult.OK) return;
                    crop = selector.TakeSelection();
                    region = selector.ScreenSelection;
                }
                if (!IsDisposed) Show();
                token.ThrowIfCancellationRequested();
                var timer = Stopwatch.StartNew();
                var database = await bank;
                var engine = await ocr;
                token.ThrowIfCancellationRequested();
                var search = await Task.Run(() => ImageSearch.Run(database, engine, crop, token));
                var result = search.Result;
                token.ThrowIfCancellationRequested();
                if (version != generation || IsDisposed) return;
                Program.Log("search " + result.Reason + " attempts=" + search.Attempts + " total_ms=" + timer.ElapsedMilliseconds + " score=" + result.Score.ToString("F3") + " margin=" + result.Margin.ToString("F3")
                    + (result.Question == null ? "" : " type=" + result.Question.Kind + " question=" + result.Question.Id));
                if (!string.IsNullOrEmpty(result.Answer))
                {
                    answer = new AnswerForm(result.Answer, region);
                    answer.Show(this);
                }
                else System.Media.SystemSounds.Asterisk.Play();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (version == generation && !IsDisposed) Program.ReportError(ex); }
            finally
            {
                if (crop != null) crop.Dispose();
                if (!IsDisposed && version == generation)
                {
                    Show(); capture.Enabled = true;
                    if (current == source) current = null;
                }
                source.Dispose();
            }
        }
    }

    internal sealed class SelectionForm : Form
    {
        private readonly Bitmap desktop;
        private readonly Rectangle screen;
        private Point origin;
        private Rectangle selected;
        private bool dragging;
        public Rectangle ScreenSelection { get { return new Rectangle(selected.X + screen.X, selected.Y + screen.Y, selected.Width, selected.Height); } }
        public SelectionForm()
        {
            Text = "截图框选";
            AutoScaleMode = AutoScaleMode.None;
            screen = SystemInformation.VirtualScreen;
            desktop = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppArgb);
            try { using (var g = Graphics.FromImage(desktop)) g.CopyFromScreen(screen.Location, Point.Empty, screen.Size, CopyPixelOperation.SourceCopy); }
            catch { desktop.Dispose(); throw; }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = screen;
            TopMost = true;
            ShowInTaskbar = true;
            DoubleBuffered = true;
            KeyPreview = true;
            Cursor = Cursors.Cross;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(desktop, 0, 0);
            using (var brush = new SolidBrush(Color.FromArgb(105, 12, 20, 32)))
            using (var dark = new Region(ClientRectangle))
            {
                if (selected.Width > 0) dark.Exclude(selected);
                e.Graphics.FillRegion(brush, dark);
            }
            if (selected.Width > 0)
                using (var pen = new Pen(Color.FromArgb(68, 149, 255), 2)) e.Graphics.DrawRectangle(pen, selected);
            var monitor = Screen.FromPoint(Cursor.Position).Bounds;
            string hint = "框选题目和选项 · Esc / 右键取消";
            using (var font = new Font("Microsoft YaHei UI", 11))
            {
                int x = monitor.X - screen.X + 24, y = monitor.Y - screen.Y + 24;
                var size = TextRenderer.MeasureText(hint, font);
                e.Graphics.FillRectangle(Brushes.Black, x - 10, y - 8, size.Width + 20, size.Height + 16);
                TextRenderer.DrawText(e.Graphics, hint, font, new Point(x, y), Color.White);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
            if (e.Button != MouseButtons.Left) return;
            origin = e.Location; dragging = true; Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            int x = Math.Max(0, Math.Min(ClientSize.Width, e.X)), y = Math.Max(0, Math.Min(ClientSize.Height, e.Y));
            selected = Rectangle.FromLTRB(Math.Min(origin.X, x), Math.Min(origin.Y, y), Math.Max(origin.X, x), Math.Max(origin.Y, y));
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !dragging) return;
            OnMouseMove(e); dragging = false; Capture = false;
            if (selected.Width < 24 || selected.Height < 16) { selected = Rectangle.Empty; Invalidate(); return; }
            DialogResult = DialogResult.OK; Close();
        }
        public Bitmap TakeSelection() { return desktop.Clone(selected, PixelFormat.Format32bppArgb); }
        protected override void Dispose(bool disposing) { if (disposing) desktop.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class AnswerForm : Form
    {
        private readonly Label label;
        public AnswerForm(string text, Rectangle selection)
        {
            Text = "";
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            KeyPreview = true;
            int fontSize = text.Length <= 6 ? 28 : 16;
            label = new Label { Text = text, Font = new Font("Microsoft YaHei UI", fontSize, FontStyle.Bold), ForeColor = Color.FromArgb(20, 27, 39), BackColor = Color.White, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill, Padding = new Padding(18), AutoEllipsis = false };
            var area = Screen.FromRectangle(selection).WorkingArea;
            var measured = TextRenderer.MeasureText(text, label.Font, new Size(Math.Min(540, area.Width - 40), 0), TextFormatFlags.WordBreak);
            ClientSize = new Size(Math.Max(120, Math.Min(area.Width - 24, measured.Width + 40)), Math.Max(76, Math.Min(area.Height - 24, measured.Height + 36)));
            Controls.Add(label);
            int x = selection.Right + 12, y = selection.Top;
            if (x + Width > area.Right) x = selection.Left - Width - 12;
            if (x < area.Left) { x = selection.Left; y = selection.Bottom + 12; }
            Location = new Point(Math.Max(area.Left + 8, Math.Min(area.Right - Width - 8, x)), Math.Max(area.Top + 8, Math.Min(area.Bottom - Height - 8, y)));
            label.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } };
            label.DoubleClick += (s, e) => Hide();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; } }
    }
}

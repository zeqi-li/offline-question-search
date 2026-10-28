using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OfflineQuestions
{
    internal static class Diagnostics
    {
        public static string QuestionText(Question q, bool reverse)
        {
            var b = new StringBuilder("1、" + q.Stem + "\n");
            var options = reverse ? q.Options.Values.Reverse().ToArray() : q.Options.Values.ToArray();
            for (int i = 0; i < options.Length; i++) b.Append((char)('A' + i)).Append("、").Append(options[i]).Append('\n');
            return b.ToString();
        }
        private static string Expected(Question q, bool reverse)
        {
            if (q.Options.Count == 0) return q.PureAnswer();
            return string.Join("\n", q.Answer.Select(c => new { Label = reverse ? (char)('A' + q.Options.Count - 1 - (c - 'A')) : c, Text = q.Options[c.ToString()] })
                .OrderBy(p => p.Label).Select(p => p.Label + "、" + p.Text));
        }
        public static Bitmap RenderText(string text, int pixelSize, int width, bool dark)
        {
            using (var font = new Font("Microsoft YaHei UI", pixelSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var temp = new Bitmap(1, 1))
            using (var measure = Graphics.FromImage(temp))
            {
                int height = (int)Math.Ceiling(measure.MeasureString(text, font, width - 56).Height) + 56;
                var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(image))
                {
                    g.Clear(dark ? Color.FromArgb(25, 27, 31) : Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    g.DrawString(text, font, dark ? Brushes.White : Brushes.Black, new RectangleF(28, 28, width - 56, height - 40));
                }
                return image;
            }
        }
        public static void ShowFixture()
        {
            string folder = Program.Root;
            if (Directory.GetFiles(folder, "*.docx").Length == 0) folder = Directory.GetParent(folder.TrimEnd(Path.DirectorySeparatorChar)).FullName;
            var bank = QuestionBank.Load(folder);
            var q = bank.Questions.First(x => x.Kind == QuestionKind.SingleChoice && x.Id.EndsWith("/14"));
            var image = RenderText(QuestionText(q, false), 24, 1000, false);
            var form = new Form { Text = "搜题测试题面", StartPosition = FormStartPosition.Manual, Location = new Point(80, 220), ClientSize = image.Size, BackColor = Color.White };
            form.Controls.Add(new PictureBox { Image = image, Dock = DockStyle.Fill });
            form.FormClosed += (s, e) => image.Dispose();
            Application.Run(form);
        }
        public static void MeasureIdle()
        {
            var form = new MainForm();
            form.Shown += async (s, e) =>
            {
                await System.Threading.Tasks.Task.Delay(2000);
                var process = Process.GetCurrentProcess();
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var timer = Stopwatch.StartNew();
                await System.Threading.Tasks.Task.Delay(4000);
                process.Refresh();
                var report = new { idle_window_ms = timer.ElapsedMilliseconds, cpu_ms = (process.TotalProcessorTime - cpu).TotalMilliseconds,
                    working_set_mb = Math.Round(process.WorkingSet64 / 1048576.0, 1), private_mb = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1),
                    scope = "Idle application, bank and Chinese OCR engine loaded, no capture or OCR requests." };
                Directory.CreateDirectory(Path.Combine(Program.Root, "verification"));
                File.WriteAllText(Path.Combine(Program.Root, "verification", "idle.json"), new JavaScriptSerializer().Serialize(report), Encoding.UTF8);
                form.Close();
            };
            Application.Run(form);
        }
        public static int Run(string path)
        {
            var data = new Dictionary<string, object>();
            bool passed = true;
            try
            {
                var watch = Stopwatch.StartNew();
                var bank = QuestionBank.Load(Program.Root);
                data["bank_questions"] = bank.Questions.Count;
                data["bank_load_ms"] = watch.ElapsedMilliseconds;
                data["bank_warnings"] = bank.Warnings;
                data["types"] = bank.Questions.GroupBy(q => q.Options.Count > 0 ? (q.Answer.Length == 1 ? "single" : "multiple") : (q.Answer == "正确" || q.Answer == "错误" ? "judgment" : "fill"))
                    .ToDictionary(g => g.Key, g => g.Count());
                data["conflicting_stems"] = bank.Questions.GroupBy(q => q.NormalStem).Where(g => g.Select(q => q.Signature).Distinct().Count() > 1).Select(g => g.Select(q => q.Id).ToArray()).ToArray();
                var exactMiss = new List<object>();
                var exactWrong = new List<object>();
                var shuffledWrong = new List<object>();
                var timings = new List<long>();
                int exactCorrect = 0, shuffledCorrect = 0, noisyCorrect = 0, noisyWrong = 0;
                foreach (var q in bank.Questions)
                {
                    watch.Restart();
                    var result = bank.Search(QuestionText(q, false));
                    timings.Add(watch.ElapsedMilliseconds);
                    if (result.Question == null) exactMiss.Add(new { id = q.Id, result.Reason, result.Score, result.Margin });
                    else if (result.Question.Signature != q.Signature || result.Answer != Expected(q, false)) exactWrong.Add(new { id = q.Id, got = result.Question.Id, answer = result.Answer, expected = Expected(q, false) });
                    else exactCorrect++;
                    if (q.Options.Count > 0)
                    {
                        var shuffled = bank.Search(QuestionText(q, true));
                        if (shuffled.Question != null && shuffled.Question.Signature == q.Signature && shuffled.Answer == Expected(q, true)) shuffledCorrect++;
                        else shuffledWrong.Add(new { id = q.Id, got = shuffled.Answer, expected = Expected(q, true), shuffled.Reason });
                    }
                    string source = QuestionText(q, false);
                    var chars = source.ToCharArray();
                    for (int i = 38; i < chars.Length; i += 71) if (char.IsLetterOrDigit(chars[i]) && chars[i] > 127) chars[i] = '口';
                    var noisy = bank.Search(new string(chars));
                    if (noisy.Question != null && noisy.Question.Signature == q.Signature) noisyCorrect++;
                    else if (noisy.Question != null) noisyWrong++;
                }
                timings.Sort();
                data["exact_correct"] = exactCorrect;
                data["exact_misses"] = exactMiss;
                data["exact_wrong"] = exactWrong;
                data["shuffled_correct"] = shuffledCorrect;
                data["shuffled_failures"] = shuffledWrong;
                data["synthetic_noisy_correct"] = noisyCorrect;
                data["synthetic_noisy_wrong"] = noisyWrong;
                data["search_p50_ms"] = timings[timings.Count / 2];
                data["search_p95_ms"] = timings[(int)(timings.Count * 0.95)];
                var unknown = new[] { "", "设置网络保存文件截图关闭", "今天午饭吃什么苹果香蕉西瓜", "已知函数f(x)=x的平方求导数", "答案A", "第2题 单选题" };
                int rejected = unknown.Count(t => bank.Search(t).Question == null);
                data["unrelated_rejected"] = rejected;
                data["unrelated_total"] = unknown.Length;
                passed &= exactWrong.Count == 0 && exactCorrect == bank.Questions.Count && shuffledWrong.Count == 0 && noisyWrong == 0 && rejected == unknown.Length && bank.Warnings.Count == 0;

                var fourteen = bank.Questions.First(q => q.Kind == QuestionKind.SingleChoice && q.Id.EndsWith("/14"));
                string fourteenText = QuestionText(fourteen, false);
                var regressions = new List<object>();
                var variants = new[] {
                    fourteenText,
                    fourteenText.Replace("A、", "Ａ）").Replace("B、", "B").Replace("C、", "C ").Replace("D、", "○ D、"),
                    fourteenText.Replace("C、3项", "3顶"),
                    fourteenText.Replace("A、", "").Replace("B、", "").Replace("C、", "").Replace("D、", ""),
                    fourteenText.Replace("A、1项\nB、2项\nC、3项\nD、4项", "A 、1项   B ）2项   C 、3项   D 、4项")
                };
                for (int i = 0; i < variants.Length; i++)
                {
                    var found = bank.Search(variants[i]);
                    bool correct = found.Question != null && found.Question.Kind == QuestionKind.SingleChoice && found.Answer == "C、3项";
                    passed &= correct;
                    regressions.Add(new { variant = i, correct, found.Answer, found.Reason, found.Score });
                }
                var fillFourteen = bank.Questions.First(q => q.Kind == QuestionKind.Fill && q.Id.EndsWith("/14"));
                string wrongType = TextTools.StripType(fillFourteen.Stem) + "\nA、政治标准\nB、经济标准\nC、年龄标准\nD、学历标准";
                var guarded = bank.Search(wrongType);
                bool protectedType = guarded.Question == null || guarded.Question.Options.Count > 0;
                passed &= protectedType;
                data["choice_type_guard"] = protectedType;
                bool numericGuard = TextTools.OptionSimilarity("3项", "3顶") >= 0.80 && TextTools.OptionSimilarity("3项", "2顶") == 0
                    && TextTools.OptionSimilarity("1亿元", "1万元") < 0.72;
                data["numeric_option_guard"] = numericGuard;
                passed &= numericGuard;
                data["question_14_regressions"] = regressions;

                var engine = new OcrService();
                data["ocr_language"] = engine.Language;
                var examples = new[] {
                    bank.Questions[1],
                    bank.Questions.First(q => q.Options.Count > 0 && q.Answer.Length > 1),
                    bank.Questions.First(q => q.Options.Count == 0 && q.Answer != "正确" && q.Answer != "错误"),
                    bank.Questions.Last(q => q.Answer == "错误"),
                    bank.Questions.First()
                };
                examples = examples.Concat(new[] { fourteen, fourteen }).ToArray();
                var ocrCases = new List<object>();
                string output = Path.GetDirectoryName(Path.GetFullPath(path));
                Directory.CreateDirectory(output);
                for (int i = 0; i < examples.Length; i++)
                {
                    var q = examples[i];
                    bool reverse = i < 5;
                    using (var image = RenderText(QuestionText(q, reverse), i == 4 || i == 6 ? 18 : 24, 1000, i == 3))
                    {
                        image.Save(Path.Combine(output, "ocr-" + i + ".png"), ImageFormat.Png);
                        watch.Restart();
                        var search = ImageSearch.Run(bank, engine, image, CancellationToken.None).GetAwaiter().GetResult();
                        string text = search.Text;
                        var result = search.Result;
                        bool correct = result.Question != null && result.Question.Signature == q.Signature && result.Answer == Expected(q, reverse);
                        passed &= correct;
                        ocrCases.Add(new { id = q.Id, image.Width, image.Height, milliseconds = watch.ElapsedMilliseconds, search.Attempts, correct, answer = result.Answer, expected = Expected(q, reverse), result.Reason, result.Score, result.Margin, text });
                    }
                }
                using (var cancelled = new CancellationTokenSource())
                using (var image = new Bitmap(64, 64))
                {
                    cancelled.Cancel();
                    try { engine.RecognizeAsync(image, cancelled.Token).GetAwaiter().GetResult(); data["cancellation"] = false; passed = false; }
                    catch (OperationCanceledException) { data["cancellation"] = true; }
                }
                data["ocr_cases"] = ocrCases;
                data["working_set_mb"] = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1);
                data["peak_working_set_mb"] = Math.Round(Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, 1);
                data["scope"] = "Local bank retrieval and generated-image OCR tests; timings exclude manual region selection and are not a guarantee for arbitrary screenshots.";
            }
            catch (Exception ex) { data["error"] = ex.ToString(); passed = false; }
            data["passed"] = passed;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(data), Encoding.UTF8);
            return passed ? 0 : 1;
        }
    }
    internal static class FixtureProgram
    {
        [STAThread]
        public static void Main()
        {
            Native.EnableDpi();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Diagnostics.ShowFixture();
        }
    }
}

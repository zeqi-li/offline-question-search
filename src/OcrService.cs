using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace OfflineQuestions
{
    internal sealed class OcrService
    {
        private readonly OcrEngine engine;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public string Language { get { return engine.RecognizerLanguage.LanguageTag; } }
        public OcrService()
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"));
            if (engine == null) throw new InvalidOperationException("Windows 缺少简体中文 OCR 组件。请在 Windows 的语言选项中安装简体中文文字识别组件后重试。");
        }
        public async Task<string> RecognizeAsync(Bitmap original, CancellationToken token, bool enhance = false)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                int limit = (int)Math.Min(2600, OcrEngine.MaxImageDimension);
                double scale = Math.Min(enhance ? 1.75 : 1.0, (double)limit / Math.Max(original.Width, original.Height));
                using (var prepared = new Bitmap(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale)), PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(prepared))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = scale != 1 ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
                        g.DrawImage(original, new Rectangle(0, 0, prepared.Width, prepared.Height));
                    }
                    var bits = prepared.LockBits(new Rectangle(0, 0, prepared.Width, prepared.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    byte[] bytes;
                    try
                    {
                        bytes = new byte[prepared.Width * prepared.Height * 4];
                        for (int y = 0; y < prepared.Height; y++)
                            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), bytes, y * prepared.Width * 4, prepared.Width * 4);
                    }
                    finally { prepared.UnlockBits(bits); }
                    // Dark-mode screenshots are converted in memory to dark text on light ground.
                    int bright = 0, samples = 0;
                    for (int y = 0; y < prepared.Height; y += Math.Max(1, prepared.Height / 12))
                    {
                        int offset = y * prepared.Width * 4;
                        bright += bytes[offset] + bytes[offset + 1] + bytes[offset + 2]; samples += 3;
                    }
                    if (bright / Math.Max(1, samples) < 110)
                        for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = (byte)(255 - bytes[i]); bytes[i + 1] = (byte)(255 - bytes[i + 1]); bytes[i + 2] = (byte)(255 - bytes[i + 2]); }
                    using (var writer = new DataWriter())
                    {
                        writer.WriteBytes(bytes);
                        using (var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8, prepared.Width, prepared.Height, BitmapAlphaMode.Ignore))
                        {
                            var completion = new TaskCompletionSource<OcrResult>();
                            var operation = engine.RecognizeAsync(bitmap);
                            operation.Completed = (finished, status) =>
                            {
                                try { completion.TrySetResult(finished.GetResults()); }
                                catch (Exception ex) { completion.TrySetException(ex); }
                                finally { finished.Close(); }
                            };
                            var result = await completion.Task.ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            return ReconstructLines(result);
                        }
                    }
                }
            }
            finally { gate.Release(); }
        }
        private sealed class Row
        {
            public double Center;
            public readonly List<OcrWord> Words = new List<OcrWord>();
        }
        private static string ReconstructLines(OcrResult result)
        {
            var words = result.Lines.SelectMany(line => line.Words).OrderBy(word => word.BoundingRect.Y + word.BoundingRect.Height / 2).ToArray();
            if (words.Length == 0) return "";
            var heights = words.Select(word => word.BoundingRect.Height).OrderBy(h => h).ToArray();
            double tolerance = Math.Max(4, heights[heights.Length / 2] * 0.58);
            var rows = new List<Row>();
            foreach (var word in words)
            {
                double center = word.BoundingRect.Y + word.BoundingRect.Height / 2;
                Row row = rows.LastOrDefault();
                if (row == null || center - row.Center > tolerance) { row = new Row { Center = center }; rows.Add(row); }
                row.Center = (row.Center * row.Words.Count + center) / (row.Words.Count + 1);
                row.Words.Add(word);
            }
            return string.Join("\n", rows.Select(row => string.Join(" ", row.Words.OrderBy(word => word.BoundingRect.X).Select(word => word.Text))));
        }
    }

    internal sealed class ImageSearch
    {
        public SearchResult Result;
        public string Text;
        public int Attempts;
        public long Milliseconds;
        public static async Task<ImageSearch> Run(QuestionBank bank, OcrService ocr, Bitmap image, CancellationToken token)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string text = await ocr.RecognizeAsync(image, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = bank.Search(text);
            int attempts = 1;
            if (string.IsNullOrEmpty(result.Answer))
            {
                token.ThrowIfCancellationRequested();
                text = await ocr.RecognizeAsync(image, token, true).ConfigureAwait(false);
                result = bank.Search(text, result.ChoiceDetected || (result.Question != null && result.Question.Options.Count > 0));
                attempts++;
            }
            token.ThrowIfCancellationRequested();
            return new ImageSearch { Result = result, Text = text, Attempts = attempts, Milliseconds = timer.ElapsedMilliseconds };
        }
    }
}

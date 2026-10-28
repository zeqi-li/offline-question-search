using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OfflineQuestions
{
    internal enum QuestionKind { Unknown, SingleChoice, MultipleChoice, Fill, Judgment }

    internal sealed class Question
    {
        public string Id, Stem, Answer, NormalStem, Signature;
        public readonly SortedDictionary<string, string> Options = new SortedDictionary<string, string>();
        public HashSet<string> Grams;
        public double Weight;
        public QuestionKind Kind
        {
            get { return Options.Count > 0 ? (Answer.Length == 1 ? QuestionKind.SingleChoice : QuestionKind.MultipleChoice)
                : (Answer == "正确" || Answer == "错误" ? QuestionKind.Judgment : QuestionKind.Fill); }
        }
        public string PureAnswer()
        {
            if (!Regex.IsMatch(Answer, "^[A-H]+$")) return Answer.Trim().TrimEnd('；', ';');
            var values = Answer.Select(c => c.ToString()).Where(Options.ContainsKey).Select(k => Options[k]).ToArray();
            return values.Length == Answer.Length ? string.Join("\n", values) : Answer;
        }
    }

    internal static class TextTools
    {
        private static readonly Regex Types = new Regex(@"[（(]?\s*(?:单项选择题|多项选择题|单选题|多选题|选择题|填空题|判断题)\s*[）)]?", RegexOptions.Compiled);
        public static string StripType(string text) { return Types.Replace(text, ""); }
        public static string Normalize(string text)
        {
            var b = new StringBuilder(text.Length);
            foreach (char c in StripType(text).Normalize(NormalizationForm.FormKC))
                if (char.IsLetterOrDigit(c)) b.Append(char.ToLowerInvariant(c));
            return StripType(b.ToString());
        }
        public static HashSet<string> Grams(string text)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < text.Length; i++) result.Add(text.Substring(i, 2));
            return result;
        }
        public static double EditSimilarity(string a, string b)
        {
            if (a == b) return 1;
            if (a.Length == 0 || b.Length == 0) return 0;
            if (a.Length > 1600) a = a.Substring(0, 1600);
            if (b.Length > 1600) b = b.Substring(0, 1600);
            int[] prev = new int[b.Length + 1], next = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                next[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    next[j] = Math.Min(Math.Min(next[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                var swap = prev; prev = next; next = swap;
            }
            return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
        }
        public static double OptionSimilarity(string a, string b)
        {
            a = Normalize(a); b = Normalize(b);
            var numbersA = Regex.Matches(a, @"\d+").Cast<Match>().Select(m => m.Value).ToArray();
            var numbersB = Regex.Matches(b, @"\d+").Cast<Match>().Select(m => m.Value).ToArray();
            if (!numbersA.SequenceEqual(numbersB)) return 0;
            double similarity = EditSimilarity(a, b);
            if (Regex.IsMatch(a, @"^\d+项$") && similarity >= 0.45 && Math.Max(a.Length, b.Length) <= 6)
                similarity = Math.Max(similarity, 0.86);
            return similarity;
        }
    }

    internal sealed class Query
    {
        private static readonly Regex Option = new Regex(@"^\s*[○◯●〇□☐☑✓✔·•\-]?\s*[\[【（(]?\s*([A-Ha-h])(?:[）)\]】]\s*|\s*[、.．:：,，]\s*|\s+|(?=[\u3400-\u9fff0-9])|$)(.*)$", RegexOptions.Compiled);
        public string Stem;
        public QuestionKind MarkedKind;
        public readonly List<string> Lines = new List<string>();
        public readonly SortedDictionary<string, string> Options = new SortedDictionary<string, string>();
        public bool BinaryJudgment
        {
            get { return Options.Count >= 2 && Options.Values.All(v => new[] { "正确", "错误", "对", "错", "是", "否" }.Contains(TextTools.Normalize(v))); }
        }
        public bool HasChoiceEvidence { get { return !BinaryJudgment && (Options.Count >= 2 || MarkedKind == QuestionKind.SingleChoice || MarkedKind == QuestionKind.MultipleChoice); } }
        public static Query Parse(string text)
        {
            var query = new Query();
            text = text.Normalize(NormalizationForm.FormKC);
            string compact = Regex.Replace(text, @"\s+", "");
            if (compact.Contains("单选题") || compact.Contains("单项选择题")) query.MarkedKind = QuestionKind.SingleChoice;
            else if (compact.Contains("多选题") || compact.Contains("多项选择题")) query.MarkedKind = QuestionKind.MultipleChoice;
            else if (compact.Contains("判断题")) query.MarkedKind = QuestionKind.Judgment;
            else if (compact.Contains("填空题")) query.MarkedKind = QuestionKind.Fill;
            var stem = new StringBuilder();
            string active = null;
            text = Regex.Replace(text, @"(?<=[^\r\n])\s+(?=[A-Ha-h]\s*[、.．:：)）])", "\n");
            foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                query.Lines.Add(line);
                var m = Option.Match(line);
                if (m.Success)
                {
                    active = m.Groups[1].Value.ToUpperInvariant();
                    query.Options[active] = m.Groups[2].Value;
                }
                else if (active != null) query.Options[active] += line;
                else stem.AppendLine(line);
            }
            string cleaned = TextTools.StripType(stem.ToString());
            cleaned = Regex.Replace(cleaned, @"^\s*(?:第\s*)?\d+\s*[、.．:：)）]\s*", "");
            query.Stem = TextTools.Normalize(cleaned);
            return query;
        }

        public SortedDictionary<string, string> ScreenOptions(Question question)
        {
            if (Options.Count >= question.Options.Count) return Options;
            var positions = new Dictionary<string, int>();
            foreach (var option in question.Options)
            {
                var matches = Lines.Select((line, index) => new { Line = line, Index = index, Match = Option.Match(line) })
                    .Select(row => new { row.Index, Score = TextTools.OptionSimilarity(option.Value, row.Match.Success ? row.Match.Groups[2].Value : row.Line) })
                    .OrderByDescending(row => row.Score).ToList();
                if (matches.Count == 0 || matches[0].Score < 0.80 || (matches.Count > 1 && matches[0].Score - matches[1].Score < 0.10)) return Options;
                if (positions.Values.Contains(matches[0].Index)) return Options;
                positions[option.Key] = matches[0].Index;
            }
            var ordered = positions.OrderBy(p => p.Value).ToArray();
            var recovered = new SortedDictionary<string, string>();
            for (int i = 0; i < ordered.Length; i++)
            {
                string label = ((char)('A' + i)).ToString();
                var marker = Option.Match(Lines[ordered[i].Value]);
                if (marker.Success && marker.Groups[1].Value.ToUpperInvariant() != label) return Options;
                recovered[label] = question.Options[ordered[i].Key];
            }
            return recovered;
        }
    }

    internal sealed class SearchResult
    {
        public Question Question;
        public string Answer, Reason;
        public double Score, Margin;
        public long Milliseconds;
        public bool ChoiceDetected;
    }

    internal sealed class QuestionBank
    {
        private static readonly Regex Start = new Regex(@"^\s*(\d+)\s*[、.．]\s*(.+)$", RegexOptions.Compiled);
        private static readonly Regex Option = new Regex(@"^\s*([A-Ha-h])\s*[、.．:：]\s*(.*)$", RegexOptions.Compiled);
        private static readonly Regex AnswerLine = new Regex(@"^\s*(?:参考|正确)?答案\s*[:：]\s*(.+)$", RegexOptions.Compiled);
        public readonly List<Question> Questions = new List<Question>();
        public readonly List<string> Warnings = new List<string>();
        private readonly Dictionary<string, List<int>> inverted = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> weights = new Dictionary<string, double>(StringComparer.Ordinal);

        public static QuestionBank Load(string folder)
        {
            var bank = new QuestionBank();
            foreach (var file in Directory.GetFiles(folder, "*.docx").Where(f => !Path.GetFileName(f).StartsWith("~$")).OrderBy(f => f))
                bank.ReadDocx(file);
            if (bank.Questions.Count == 0) throw new InvalidDataException("程序所在文件夹没有可读取的题库。请放入带有“答案：”的 DOCX 题库。");
            for (int i = 0; i < bank.Questions.Count; i++)
            {
                var q = bank.Questions[i];
                q.NormalStem = TextTools.Normalize(q.Stem);
                q.Signature = TextTools.Normalize(q.PureAnswer());
                q.Grams = TextTools.Grams(q.NormalStem);
                foreach (string gram in q.Grams)
                {
                    List<int> ids;
                    if (!bank.inverted.TryGetValue(gram, out ids)) bank.inverted[gram] = ids = new List<int>();
                    ids.Add(i);
                }
            }
            foreach (var pair in bank.inverted) bank.weights[pair.Key] = 1 + Math.Log(1 + (double)bank.Questions.Count / pair.Value.Count);
            foreach (var q in bank.Questions) q.Weight = q.Grams.Sum(g => bank.weights[g]);
            return bank;
        }

        private void ReadDocx(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) throw new InvalidDataException(Path.GetFileName(file) + " 不是有效的 DOCX 文档。");
                XDocument doc;
                using (var xml = entry.Open()) doc = XDocument.Load(xml);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                Question current = null;
                string section = "", activeOption = null;
                int row = 0;
                foreach (var paragraph in doc.Descendants(w + "body").Descendants(w + "p"))
                {
                    row++;
                    var text = string.Concat(paragraph.Descendants().Where(e => e.Name == w + "t" || e.Name == w + "br" || e.Name == w + "tab")
                        .Select(e => e.Name == w + "t" ? e.Value : " ")).Trim();
                    if (text.Length == 0) continue;
                    var answer = AnswerLine.Match(text);
                    var start = Start.Match(text);
                    var option = Option.Match(text);
                    if (answer.Success)
                    {
                        if (current == null) { Warnings.Add(Path.GetFileName(file) + ":" + row + " 孤立答案"); continue; }
                        current.Answer = answer.Groups[1].Value.Trim().TrimEnd('；', ';');
                        if (Regex.IsMatch(current.Answer, @"^[A-Ha-h\s、,，]+$"))
                            current.Answer = Regex.Replace(current.Answer.ToUpperInvariant(), "[^A-H]", "");
                        if (current.Answer == "对") current.Answer = "正确";
                        if (current.Answer == "错") current.Answer = "错误";
                        Questions.Add(current);
                        current = null; activeOption = null;
                    }
                    else if (start.Success)
                    {
                        if (current != null) Warnings.Add(current.Id + " 缺少答案，已跳过");
                        current = new Question { Id = Path.GetFileName(file) + "/" + section + "/" + start.Groups[1].Value, Stem = start.Groups[2].Value };
                        activeOption = null;
                    }
                    else if (current != null && option.Success)
                    {
                        activeOption = option.Groups[1].Value.ToUpperInvariant();
                        current.Options[activeOption] = option.Groups[2].Value;
                    }
                    else if (current != null)
                    {
                        if (activeOption != null) current.Options[activeOption] += text;
                        else current.Stem += text;
                    }
                    else if (!text.StartsWith("解析") && (text.Contains("单选题") || text.Contains("多选题") || text.Contains("题库"))) section = text;
                }
                if (current != null) Warnings.Add(current.Id + " 缺少答案，已跳过");
            }
        }

        private sealed class Candidate
        {
            public Question Question; public double Score, StemScore, Edit; public bool Exact;
            public SortedDictionary<string, string> ScreenOptions;
        }
        public SearchResult Search(string recognized, bool requireChoice = false)
        {
            var watch = Stopwatch.StartNew();
            var query = Query.Parse(recognized);
            bool choiceDetected = requireChoice || query.HasChoiceEvidence;
            var miss = new SearchResult { Reason = "no-match", ChoiceDetected = choiceDetected };
            if (query.Stem.Length < 7 || query.Stem.Length > 2000) { miss.Reason = "text-length"; return miss; }
            var grams = TextTools.Grams(query.Stem);
            var votes = new Dictionary<int, double>();
            double queryWeight = 0;
            foreach (var g in grams)
            {
                List<int> ids;
                double weight;
                if (inverted.TryGetValue(g, out ids))
                {
                    weight = weights[g];
                    foreach (int id in ids) { double v; votes.TryGetValue(id, out v); votes[id] = v + weight; }
                }
                else weight = 1 + Math.Log(1 + Questions.Count);
                queryWeight += weight;
            }
            var ranked = new List<Candidate>();
            foreach (var vote in votes.OrderByDescending(p => p.Value / Math.Sqrt(Math.Max(1, Questions[p.Key].Weight))).Take(24))
            {
                var q = Questions[vote.Key];
                if ((choiceDetected && q.Options.Count == 0) ||
                    (query.BinaryJudgment && q.Kind != QuestionKind.Judgment) ||
                    (query.MarkedKind == QuestionKind.SingleChoice && q.Kind != QuestionKind.SingleChoice) ||
                    (query.MarkedKind == QuestionKind.MultipleChoice && q.Kind != QuestionKind.MultipleChoice) ||
                    (!choiceDetected && query.MarkedKind == QuestionKind.Fill && q.Kind != QuestionKind.Fill) ||
                    (!choiceDetected && query.MarkedKind == QuestionKind.Judgment && q.Kind != QuestionKind.Judgment)) continue;
                double dice = 2 * vote.Value / Math.Max(1, q.Weight + queryWeight);
                double edit = TextTools.EditSimilarity(q.NormalStem, query.Stem);
                double stemScore = 0.58 * dice + 0.42 * edit;
                if (query.Stem.Contains(q.NormalStem) && q.NormalStem.Length >= 10)
                    stemScore = Math.Max(stemScore, 0.985 - Math.Min(0.10, (query.Stem.Length - q.NormalStem.Length) * 0.00035));
                if (q.NormalStem.Contains(query.Stem) && query.Stem.Length >= 18 && query.Stem.Length >= q.NormalStem.Length * 0.72)
                    stemScore = Math.Max(stemScore, 0.95);
                double score = stemScore;
                var screenOptions = q.Options.Count > 0 ? query.ScreenOptions(q) : query.Options;
                if (screenOptions.Count >= 2 && q.Options.Count >= 2)
                {
                    double optionScore = q.Options.Values.Average(v => screenOptions.Values.Max(x => TextTools.OptionSimilarity(v, x)));
                    score = 0.86 * stemScore + 0.14 * optionScore;
                }
                bool exactOptions = screenOptions.Count == q.Options.Count && q.Options.Values.Select(TextTools.Normalize).OrderBy(x => x)
                    .SequenceEqual(screenOptions.Values.Select(TextTools.Normalize).OrderBy(x => x));
                ranked.Add(new Candidate { Question = q, Score = score, StemScore = stemScore, Edit = edit,
                    ScreenOptions = screenOptions,
                    Exact = query.Stem == q.NormalStem && (query.Options.Count == 0 || exactOptions) });
            }
            if (ranked.Any(c => c.Question.Options.Count >= 2 && c.ScreenOptions.Count == c.Question.Options.Count && c.StemScore >= 0.73))
            {
                choiceDetected = true;
                ranked.RemoveAll(c => c.Question.Options.Count == 0);
            }
            ranked = ranked.OrderByDescending(c => c.Score).ToList();
            if (ranked.Count == 0) return miss;
            var best = ranked[0];
            var different = ranked.Skip(1).FirstOrDefault(c => c.Question.Signature != best.Question.Signature);
            double margin = different == null ? 1 : best.Score - different.Score;
            var result = new SearchResult { Score = best.Score, Margin = margin, Milliseconds = watch.ElapsedMilliseconds, ChoiceDetected = choiceDetected };
            bool strongText = best.StemScore >= 0.73 && best.Score >= 0.76;
            bool separatedOcr = query.Stem.Length >= 18 && best.Score >= 0.70 && best.Edit >= 0.80 && margin >= 0.20;
            if (!(strongText || separatedOcr) || (query.Stem.Length < 16 && best.StemScore < 0.94))
                result.Reason = "low-confidence";
            else if (margin < 0.045 && !(best.Exact && !ranked.Skip(1).Any(c => c.Exact && c.Question.Signature != best.Question.Signature))) result.Reason = "ambiguous";
            else
            {
                result.Question = best.Question;
                result.Answer = FormatAnswer(best.Question, best.ScreenOptions);
                result.Reason = result.Answer == null ? "option-uncertain" : "matched";
            }
            return result;
        }

        private static string FormatAnswer(Question q, SortedDictionary<string, string> options)
        {
            if (!Regex.IsMatch(q.Answer, "^[A-H]+$") || q.Options.Count == 0) return q.PureAnswer();
            var mapped = new SortedDictionary<string, string>();
            foreach (char letter in q.Answer)
            {
                string answer;
                if (!q.Options.TryGetValue(letter.ToString(), out answer)) return null;
                var values = options.Select(p => new { Label = p.Key, Score = TextTools.OptionSimilarity(answer, p.Value) })
                    .OrderByDescending(x => x.Score).ToList();
                if (values.Count == 0) return null;
                double gap = values.Count > 1 ? values[0].Score - values[1].Score : 1;
                bool uniqueExact = values[0].Score == 1 && gap > 0;
                bool strongFuzzy = values[0].Score >= 0.80 && gap >= 0.12;
                bool droppedCharacter = TextTools.Normalize(answer).Length >= 3 && values[0].Score >= 0.72 && gap >= 0.22;
                if (!(uniqueExact || strongFuzzy || droppedCharacter)) return null;
                if (mapped.ContainsKey(values[0].Label)) return null;
                mapped.Add(values[0].Label, answer);
            }
            return string.Join("\n", mapped.Select(p => p.Key + "、" + p.Value));
        }
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace School.Training;

/// <summary>Header mapping and cell parsing for question bank imports. Kept deterministic for tests.</summary>
public static class ExamImport
{
    public const int MaxSheetRows = ImportCsv.MaxSheetRows;

    public const string TemplateHeaders = "题型,题干,选项,正确答案,分值,解析";

    static readonly IReadOnlyDictionary<string, string> HeaderAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["题型"] = "type",
        ["类型"] = "type",
        ["题干"] = "stem",
        ["题目"] = "stem",
        ["选项"] = "options",
        ["正确答案"] = "answer",
        ["答案"] = "answer",
        ["分值"] = "score",
        ["解析"] = "analysis",
        ["题目解析"] = "analysis",
        ["type"] = "type",
        ["stem"] = "stem",
        ["options"] = "options",
        ["answer"] = "answer",
        ["score"] = "score",
        ["analysis"] = "analysis"
    };

    static readonly string[] RequiredHeaders = ["type", "stem"];

    static readonly Regex OptionLabel = new("^[A-Za-zＡ-Ｚａ-ｚ][.、．:：)）]\\s*", RegexOptions.CultureInvariant);
    static readonly Regex AnswerSeparators = new("[,，、;；|｜\\s]+", RegexOptions.CultureInvariant);

    public static string Template()
    {
        static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        var rows = new[]
        {
            TemplateHeaders,
            string.Join(',', new[] { "单选题", "计算机网络的英文缩写是？", "LAN|WAN|TCP|IP", "A", "5", "LAN 是局域网（Local Area Network）的缩写。" }.Select(Csv)),
            string.Join(',', new[] { "多选题", "以下属于网络互连设备的有？", "路由器|交换机|显示器|集线器", "A,B,D", "10", "显示器和主机属于终端设备。" }.Select(Csv)),
            string.Join(',', new[] { "判断题", "交换机工作在物理层。", "正确|错误", "B", "5", "交换机工作在数据链路层。" }.Select(Csv)),
            string.Join(',', new[] { "实操题", "请说明 VLAN 的主要作用。", "", "", "10", "按隔离广播域、划分逻辑网络、提升安全等要点评分。" }.Select(Csv))
        };
        return string.Join("\r\n", rows) + "\r\n";
    }

    public static List<string> MapHeaders(IReadOnlyList<string> headerRow)
        => headerRow.Select(header => HeaderAliases.GetValueOrDefault(header.Trim('\uFEFF').Trim(), "")).ToList();

    public static List<ExamImportRow> BuildRows(IReadOnlyList<List<string>> sheet)
    {
        if (sheet.Count == 0) throw new ImportSheetException("文件没有可导入的数据");
        var headers = MapHeaders(sheet[0]);
        if (RequiredHeaders.Any(header => !headers.Contains(header)))
            throw new ImportSheetException("缺少题型或题干表头，请下载导入模板");
        if (headers.Where(header => header.Length > 0).GroupBy(header => header).Any(group => group.Count() > 1))
            throw new ImportSheetException("表头包含重复列");

        var rows = new List<ExamImportRow>();
        for (var index = 1; index < sheet.Count; index++)
        {
            var cells = sheet[index];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            string Get(string header)
            {
                var position = headers.IndexOf(header);
                return position >= 0 && position < cells.Count ? cells[position].Trim() : "";
            }
            var row = new ExamImportRow { RowNumber = index + 1, Errors = [] };
            var rawType = Get("type");
            var type = ParseType(rawType);
            if (type == null) row.Errors.Add(new ImportError("题型", "题型应为单选题、多选题、判断题或实操题"));
            type ??= "single";
            var options = SplitOptions(Get("options"), type);
            var answers = type == "practical" ? [] : ParseAnswers(Get("answer"), type, options, row);
            var score = 10;
            var rawScore = Get("score");
            if (rawScore.Length > 0 && (!int.TryParse(rawScore, out score) || score is < 1 or > 100))
            {
                row.Errors.Add(new ImportError("分值", "分值应为 1–100 的整数"));
                score = 10;
            }
            var input = new ExamQuestionInput(type, Get("stem"), options, answers, score, Get("analysis"));
            var validation = ExamLogic.ValidateQuestion(input);
            if (validation != null) row.Errors.Add(new ImportError(ColumnOf(validation), validation));
            row.Data = input;
            rows.Add(row);
        }
        return rows.Count == 0 ? throw new ImportSheetException("文件没有可导入的数据") : rows;
    }

    static string ColumnOf(string message)
        => message.Contains("题型") ? "题型"
        : message.Contains("题干") ? "题干"
        : message.Contains("选项") ? "选项"
        : message.Contains("答案") || message.Contains("正确") ? "正确答案"
        : message.Contains("分值") ? "分值"
        : message.Contains("解析") ? "解析"
        : "题目";

    static string? ParseType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "单选" or "单选题" or "single" => "single",
        "多选" or "多选题" or "multiple" => "multiple",
        "判断" or "判断题" or "judge" => "judge",
        "实操" or "实操题" or "简答" or "简答题" or "practical" => "practical",
        _ => null
    };

    static string[] SplitOptions(string value, string type)
    {
        if (type == "practical") return [];
        if (value.Length == 0) return type == "judge" ? ["正确", "错误"] : [];
        return value.Split(['|', '｜'], StringSplitOptions.TrimEntries)
            .Select(option => OptionLabel.Replace(option.Trim(), "").Trim())
            .Where(option => option.Length > 0)
            .ToArray();
    }

    static int[] ParseAnswers(string value, string type, string[] options, ExamImportRow row)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<int>();
        foreach (var token in AnswerSeparators.Split(value.Trim()).Where(item => item.Length > 0))
        {
            var normalized = token.Trim().ToUpperInvariant();
            var index = Array.FindIndex(options, option => string.Equals(option, token.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index < 0 && type == "judge")
            {
                if (normalized is "正确" or "对" or "是" or "√" or "T" or "TRUE" or "A") index = 0;
                else if (normalized is "错误" or "错" or "否" or "×" or "F" or "FALSE" or "B") index = 1;
            }
            if (index < 0 && normalized.Length == 1)
            {
                var code = normalized[0];
                if (code is >= 'A' and <= 'Z') index = code - 'A';
                else if (char.IsDigit(code)) index = code - '1';
            }
            if (index >= 0 && index < options.Length)
            {
                result.Add(index);
                continue;
            }
            row.Errors.Add(new ImportError("正确答案", $"无法识别答案“{token}”"));
        }
        return ExamLogic.NormalizeSelection(result);
    }
}

public sealed class ExamImportRow
{
    public int RowNumber { get; set; }
    public ExamQuestionInput Data { get; set; } = new("single", "", [], [], 10, "");
    public List<ImportError> Errors { get; set; } = [];
}

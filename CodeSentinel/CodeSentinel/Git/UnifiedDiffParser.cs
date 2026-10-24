using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodeSentinel.Data.Entities;

namespace CodeSentinel.Git;

/// <summary>
/// 解析 git unified diff 补丁文本为结构化文件 diff，纯函数、无 I/O。
/// </summary>
public static class UnifiedDiffParser
{
    /// <summary>
    /// hunk 头格式：@@ -旧起始[,旧行数] +新起始[,新行数] @@，行数缺省为 1。
    /// </summary>
    private static readonly Regex HunkHeader = new(
        @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@",
        RegexOptions.Compiled);

    /// <summary>
    /// 解析一段 git diff 补丁，支持多文件、新增、删除、修改、重命名、中文路径与二进制标记。
    /// </summary>
    /// <param name="patch">补丁文本</param>
    /// <returns>解析出的文件 diff 列表</returns>
    public static IReadOnlyList<DiffFile> Parse(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var files = new List<DiffFile>();
        FileBuilder? current = null;

        foreach (var line in SplitLines(patch))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                if (current is not null)
                {
                    files.Add(current.Build());
                }

                var (fallbackOldPath, fallbackNewPath) = ParseDiffGitLine(line);
                current = new FileBuilder(fallbackOldPath, fallbackNewPath);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current.StartHunk(ParseHunkHeader(line));
                continue;
            }

            if (current.InHunks)
            {
                current.AddContentLine(line);
                continue;
            }

            current.TryParseHeader(line);
        }

        if (current is not null)
        {
            files.Add(current.Build());
        }

        return files;
    }

    /// <summary>
    /// 解析 hunk 头行，无法识别范围信息时抛出 FormatException。
    /// </summary>
    /// <param name="line">hunk 头行</param>
    /// <returns>旧、新文件的起始行与行数</returns>
    private static (int OldStart, int OldLines, int NewStart, int NewLines) ParseHunkHeader(string line)
    {
        var match = HunkHeader.Match(line);
        if (!match.Success)
        {
            throw new FormatException($"无法解析 hunk 头：{line}");
        }

        var oldStart = ParseInt(match.Groups[1].Value);
        var oldLines = match.Groups[2].Success ? ParseInt(match.Groups[2].Value) : 1;
        var newStart = ParseInt(match.Groups[3].Value);
        var newLines = match.Groups[4].Success ? ParseInt(match.Groups[4].Value) : 1;
        return (oldStart, oldLines, newStart, newLines);
    }

    /// <summary>
    /// 解析整数，只应接收已由正则校验过的数字串。
    /// </summary>
    /// <param name="value">数字串</param>
    /// <returns>整数值</returns>
    private static int ParseInt(string value)
    {
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 从 diff --git 行提取旧、新路径作为回退值，供没有 ---/+++ 的二进制或重命名文件使用。
    /// </summary>
    /// <param name="line">diff --git 头行</param>
    /// <returns>旧、新路径（可能为 null）</returns>
    private static (string? OldPath, string? NewPath) ParseDiffGitLine(string line)
    {
        var content = line["diff --git ".Length..];
        if (!content.StartsWith("a/", StringComparison.Ordinal))
        {
            return (null, null);
        }

        var separator = content.IndexOf(" b/", StringComparison.Ordinal);
        if (separator <= 2)
        {
            return (null, null);
        }

        var oldPath = UnquotePath(content[2..separator]);
        var newPath = UnquotePath(content[(separator + 3)..]);
        return (oldPath, newPath);
    }

    /// <summary>
    /// 解析 ---/+++ 头行中的路径：去掉 a//b/ 前缀与 git 的行尾 tab 消除符，/dev/null 返回 null。
    /// </summary>
    /// <param name="line">头行</param>
    /// <param name="prefixLength">前缀长度（"--- " 或 "+++ " 为 4）</param>
    /// <param name="pathPrefix">路径前缀（a 或 b）</param>
    /// <returns>去前缀后的路径，/dev/null 时为 null</returns>
    private static string? ParseHeaderPath(string line, int prefixLength, string pathPrefix)
    {
        var raw = line[prefixLength..].TrimEnd('\t');
        var path = UnquotePath(raw);
        if (path == "/dev/null")
        {
            return null;
        }

        var prefix = pathPrefix + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal)
            ? path[prefix.Length..]
            : path;
    }

    /// <summary>
    /// 反转义 C 风格引用的路径（以双引号包裹、反斜杠转义与八进制字节）。
    /// </summary>
    /// <param name="value">原始路径片段</param>
    /// <returns>真实路径</returns>
    private static string UnquotePath(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var inner = value.AsSpan(1, value.Length - 2);
        var bytes = new List<byte>(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            var ch = inner[i];
            if (ch != '\\')
            {
                AppendUtf8(bytes, ch);
                continue;
            }

            if (++i >= inner.Length)
            {
                break;
            }

            var escape = inner[i];
            switch (escape)
            {
                case 'a': bytes.Add(0x07); break;
                case 'b': bytes.Add(0x08); break;
                case 't': bytes.Add(0x09); break;
                case 'n': bytes.Add(0x0A); break;
                case 'v': bytes.Add(0x0B); break;
                case 'f': bytes.Add(0x0C); break;
                case 'r': bytes.Add(0x0D); break;
                case '"': bytes.Add((byte)'"'); break;
                case '\\': bytes.Add((byte)'\\'); break;
                default:
                    if (escape is >= '0' and <= '7')
                    {
                        // 1~3 位八进制转义，表示一个原始字节
                        var octet = 0;
                        var count = 0;
                        while (count < 3 && i < inner.Length && inner[i] is >= '0' and <= '7')
                        {
                            octet = (octet * 8) + (inner[i] - '0');
                            i++;
                            count++;
                        }

                        i--;
                        bytes.Add((byte)octet);
                    }
                    else
                    {
                        AppendUtf8(bytes, escape);
                    }

                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>
    /// 把一个字符按 UTF-8 追加到字节缓冲。
    /// </summary>
    /// <param name="bytes">字节缓冲</param>
    /// <param name="ch">字符</param>
    private static void AppendUtf8(List<byte> bytes, char ch)
    {
        if (ch < 0x80)
        {
            bytes.Add((byte)ch);
            return;
        }

        bytes.AddRange(Encoding.UTF8.GetBytes(ch.ToString()));
    }

    /// <summary>
    /// 按行拆分补丁，兼容 CRLF 行尾。
    /// </summary>
    /// <param name="patch">补丁文本</param>
    /// <returns>行数组</returns>
    private static string[] SplitLines(string patch)
    {
        var lines = patch.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r'))
            {
                lines[i] = lines[i][..^1];
            }
        }

        return lines;
    }

    /// <summary>
    /// 统计新增与删除行数（只数 +/− 行，不含 hunk 头）。
    /// </summary>
    /// <param name="hunks">diff 块</param>
    /// <returns>新增、删除行数</returns>
    private static (int Additions, int Deletions) CountChanges(IReadOnlyList<DiffHunk> hunks)
    {
        var additions = 0;
        var deletions = 0;
        foreach (var hunk in hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Type == DiffLineType.Add)
                {
                    additions++;
                }
                else if (line.Type == DiffLineType.Delete)
                {
                    deletions++;
                }
            }
        }

        return (additions, deletions);
    }

    /// <summary>
    /// 单个文件解析过程的中间状态。
    /// </summary>
    /// <param name="FallbackOldPath">diff --git 行提取的旧路径回退值</param>
    /// <param name="FallbackNewPath">diff --git 行提取的新路径回退值</param>
    private sealed class FileBuilder(string? FallbackOldPath, string? FallbackNewPath)
    {
        private readonly List<DiffHunk> _hunks = [];
        private HunkBuilder? _hunk;

        /// <summary>
        /// 新路径。
        /// </summary>
        public string? NewPath { get; private set; } = FallbackNewPath;

        /// <summary>
        /// 旧路径。
        /// </summary>
        public string? OldPath { get; private set; } = FallbackOldPath;

        /// <summary>
        /// 是否为二进制。
        /// </summary>
        public bool IsBinary { get; private set; }

        /// <summary>
        /// 是否已进入 hunk 内容阶段。
        /// </summary>
        public bool InHunks { get; private set; }

        private bool _isAdded;
        private bool _isDeleted;
        private bool _isRenamed;

        /// <summary>
        /// 解析文件头阶段的头部行，识别路径、状态与二进制标记。
        /// </summary>
        /// <param name="line">头部行</param>
        public void TryParseHeader(string line)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var path = ParseHeaderPath(line, 4, "a");
                if (path is null)
                {
                    _isAdded = true;
                }
                else
                {
                    OldPath = path;
                }

                return;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = ParseHeaderPath(line, 4, "b");
                if (path is null)
                {
                    _isDeleted = true;
                }
                else
                {
                    NewPath = path;
                }

                return;
            }

            if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                OldPath = UnquotePath(line["rename from ".Length..]);
                _isRenamed = true;
                return;
            }

            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                NewPath = UnquotePath(line["rename to ".Length..]);
                _isRenamed = true;
                return;
            }

            if (line.StartsWith("Binary files ", StringComparison.Ordinal)
                || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                IsBinary = true;
            }

            // 其它头部行（index、mode、similarity 等）忽略
        }

        /// <summary>
        /// 开始一个新的 hunk，先落定上一个。
        /// </summary>
        /// <param name="header">hunk 头解析结果</param>
        public void StartHunk((int OldStart, int OldLines, int NewStart, int NewLines) header)
        {
            FinishHunk();
            _hunk = new HunkBuilder(header.OldStart, header.OldLines, header.NewStart, header.NewLines);
            InHunks = true;
        }

        /// <summary>
        /// 追加 hunk 内容行，忽略 \ No newline at end of file 标记。
        /// </summary>
        /// <param name="line">内容行</param>
        public void AddContentLine(string line)
        {
            if (_hunk is null || line.StartsWith('\\'))
            {
                return;
            }

            _hunk.AddLine(line);
        }

        /// <summary>
        /// 落定当前 hunk 并产出 DiffFile。
        /// </summary>
        /// <returns>解析结果</returns>
        public DiffFile Build()
        {
            FinishHunk();
            var status = _isRenamed ? FileChangeStatus.Renamed
                : _isAdded ? FileChangeStatus.Added
                : _isDeleted ? FileChangeStatus.Deleted
                : FileChangeStatus.Modified;
            var path = NewPath ?? OldPath ?? string.Empty;
            var oldPath = _isRenamed ? OldPath : null;
            var (additions, deletions) = CountChanges(_hunks);
            return new DiffFile(path, oldPath, status, additions, deletions, IsBinary, false, _hunks);
        }

        /// <summary>
        /// 把当前 hunk 加入列表并清空。
        /// </summary>
        private void FinishHunk()
        {
            if (_hunk is null)
            {
                return;
            }

            _hunks.Add(_hunk.Build());
            _hunk = null;
        }
    }

    /// <summary>
    /// 单个 hunk 的行收集与行号推进。
    /// </summary>
    private sealed class HunkBuilder
    {
        private readonly int _oldStart;
        private readonly int _oldLines;
        private readonly int _newStart;
        private readonly int _newLines;
        private readonly List<DiffLine> _lines = [];
        private int _oldLine;
        private int _newLine;

        /// <summary>
        /// 初始化 hunk 构建器。
        /// </summary>
        /// <param name="oldStart">旧文件起始行</param>
        /// <param name="oldLines">旧文件行数</param>
        /// <param name="newStart">新文件起始行</param>
        /// <param name="newLines">新文件行数</param>
        public HunkBuilder(int oldStart, int oldLines, int newStart, int newLines)
        {
            _oldStart = oldStart;
            _oldLines = oldLines;
            _newStart = newStart;
            _newLines = newLines;
            _oldLine = oldStart;
            _newLine = newStart;
        }

        /// <summary>
        /// 追加一行内容并按类型推进行号。
        /// </summary>
        /// <param name="line">带前缀的内容行</param>
        public void AddLine(string line)
        {
            if (line.StartsWith('+'))
            {
                _lines.Add(new DiffLine(DiffLineType.Add, null, _newLine++, line[1..]));
            }
            else if (line.StartsWith('-'))
            {
                _lines.Add(new DiffLine(DiffLineType.Delete, _oldLine++, null, line[1..]));
            }
            else if (line.StartsWith(' '))
            {
                _lines.Add(new DiffLine(DiffLineType.Context, _oldLine++, _newLine++, line[1..]));
            }

            // 无法识别的行忽略
        }

        /// <summary>
        /// 产出 hunk。
        /// </summary>
        /// <returns>hunk</returns>
        public DiffHunk Build()
        {
            return new DiffHunk(_oldStart, _oldLines, _newStart, _newLines, _lines);
        }
    }
}

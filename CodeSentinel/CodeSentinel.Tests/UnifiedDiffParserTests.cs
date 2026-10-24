using CodeSentinel.Data.Entities;
using CodeSentinel.Git;

namespace CodeSentinel.Tests;

/// <summary>
/// 统一 diff 补丁解析器测试：使用字面补丁，不依赖 git。
/// </summary>
public class UnifiedDiffParserTests
{
    /// <summary>
    /// 新增、修改、删除与重命名四类文件都应被解析出正确的状态、路径与增删行数。
    /// </summary>
    [Fact]
    public void Parses_added_modified_deleted_and_renamed_files()
    {
        var patch = string.Join('\n',
            "diff --git a/added.txt b/added.txt",
            "new file mode 100644",
            "index 0000000..abc1234",
            "--- /dev/null",
            "+++ b/added.txt",
            "@@ -0,0 +1,2 @@",
            "+first",
            "+second",
            "diff --git a/modified.txt b/modified.txt",
            "index 1111111..2222222 100644",
            "--- a/modified.txt",
            "+++ b/modified.txt",
            "@@ -1,3 +1,4 @@",
            " context",
            "-old",
            "+new",
            " context2",
            "diff --git a/deleted.txt b/deleted.txt",
            "deleted file mode 100644",
            "index 3333333..0000000",
            "--- a/deleted.txt",
            "+++ /dev/null",
            "@@ -1,1 +0,0 @@",
            "-gone",
            "diff --git a/old.md b/new.md",
            "similarity index 100%",
            "rename from old.md",
            "rename to new.md");

        var files = UnifiedDiffParser.Parse(patch);

        Assert.Equal(4, files.Count);

        var added = files[0];
        Assert.Equal("added.txt", added.Path);
        Assert.Null(added.OldPath);
        Assert.Equal(FileChangeStatus.Added, added.Status);
        Assert.Equal(2, added.Additions);
        Assert.Equal(0, added.Deletions);
        Assert.False(added.IsBinary);
        var addedHunk = Assert.Single(added.Hunks);
        Assert.Equal(0, addedHunk.OldStart);
        Assert.Equal(0, addedHunk.OldLines);
        Assert.Equal(1, addedHunk.NewStart);
        Assert.Equal(2, addedHunk.NewLines);
        Assert.Equal(2, addedHunk.Lines.Count);
        Assert.All(addedHunk.Lines, m => Assert.Equal(DiffLineType.Add, m.Type));
        Assert.Equal(1, addedHunk.Lines[0].NewLine);
        Assert.Equal(2, addedHunk.Lines[1].NewLine);

        var modified = files[1];
        Assert.Equal("modified.txt", modified.Path);
        Assert.Null(modified.OldPath);
        Assert.Equal(FileChangeStatus.Modified, modified.Status);
        Assert.Equal(1, modified.Additions);
        Assert.Equal(1, modified.Deletions);
        var modifiedHunk = Assert.Single(modified.Hunks);
        Assert.Equal(1, modifiedHunk.OldStart);
        Assert.Equal(3, modifiedHunk.OldLines);
        Assert.Equal(1, modifiedHunk.NewStart);
        Assert.Equal(4, modifiedHunk.NewLines);
        Assert.Equal(
            new[]
            {
                DiffLineType.Context, DiffLineType.Delete, DiffLineType.Add, DiffLineType.Context
            },
            modifiedHunk.Lines.Select(m => m.Type).ToArray());
        Assert.Equal("old", modifiedHunk.Lines[1].Text);
        Assert.Equal("new", modifiedHunk.Lines[2].Text);

        var deleted = files[2];
        Assert.Equal("deleted.txt", deleted.Path);
        Assert.Null(deleted.OldPath);
        Assert.Equal(FileChangeStatus.Deleted, deleted.Status);
        Assert.Equal(0, deleted.Additions);
        Assert.Equal(1, deleted.Deletions);
        Assert.Single(deleted.Hunks);

        var renamed = files[3];
        Assert.Equal("new.md", renamed.Path);
        Assert.Equal("old.md", renamed.OldPath);
        Assert.Equal(FileChangeStatus.Renamed, renamed.Status);
        Assert.Equal(0, renamed.Additions);
        Assert.Equal(0, renamed.Deletions);
        Assert.Empty(renamed.Hunks);
    }

    /// <summary>
    /// 中文路径在 core.quotePath=false 下按原文输出，解析结果应保留原始路径。
    /// </summary>
    [Fact]
    public void Parses_chinese_paths_without_quoting()
    {
        var patch = string.Join('\n',
            "diff --git a/文档/说明.md b/文档/说明.md",
            "index 1111111..2222222 100644",
            "--- a/文档/说明.md",
            "+++ b/文档/说明.md",
            "@@ -1,2 +1,2 @@",
            " 第一行",
            "-旧内容",
            "+新内容");

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        Assert.Equal("文档/说明.md", file.Path);
        Assert.Null(file.OldPath);
        Assert.Equal(FileChangeStatus.Modified, file.Status);
        Assert.Equal(1, file.Additions);
        Assert.Equal(1, file.Deletions);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal("旧内容", hunk.Lines[1].Text);
        Assert.Equal("新内容", hunk.Lines[2].Text);
    }

    /// <summary>
    /// 补丁中出现 Binary files ... differ 时标记为二进制，且不产生 hunk。
    /// </summary>
    [Fact]
    public void Marks_binary_when_patch_says_Binary_files_differ()
    {
        var patch = string.Join('\n',
            "diff --git a/image.png b/image.png",
            "index 1111111..2222222 100644",
            "Binary files a/image.png and b/image.png differ");

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        Assert.Equal("image.png", file.Path);
        Assert.True(file.IsBinary);
        Assert.Empty(file.Hunks);
        Assert.Equal(0, file.Additions);
        Assert.Equal(0, file.Deletions);
    }

    /// <summary>
    /// 无法解析范围信息的 hunk 头必须抛出 FormatException。
    /// </summary>
    [Fact]
    public void Rejects_malformed_hunk_header()
    {
        var patch = string.Join('\n',
            "diff --git a/file.txt b/file.txt",
            "index 1111111..2222222 100644",
            "--- a/file.txt",
            "+++ b/file.txt",
            "@@ not-a-valid-range @@");

        var exception = Assert.Throws<FormatException>(() => UnifiedDiffParser.Parse(patch));

        Assert.Contains("@@", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 带 C 风格引用的路径（空格等特殊字符）应被反转义为真实路径。
    /// </summary>
    [Fact]
    public void Unquotes_c_style_quoted_paths()
    {
        var patch = string.Join('\n',
            "diff --git \"a/with space.txt\" \"b/with space.txt\"",
            "index 1111111..2222222 100644",
            "--- \"a/with space.txt\"",
            "+++ \"b/with space.txt\"",
            "@@ -1 +1 @@",
            "-old",
            "+new");

        var file = Assert.Single(UnifiedDiffParser.Parse(patch));

        Assert.Equal("with space.txt", file.Path);
        Assert.Equal(FileChangeStatus.Modified, file.Status);
    }
}

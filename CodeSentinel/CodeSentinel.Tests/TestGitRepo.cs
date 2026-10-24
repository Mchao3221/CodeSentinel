using System.Diagnostics;
using System.Text;

namespace CodeSentinel.Tests;

/// <summary>
/// 测试用真实 git 仓库：创建一个裸仓库作为远端，并推入 main 分支。
/// </summary>
public sealed class TestGitRepo : IDisposable
{
    private const int GitTimeoutMilliseconds = 30_000;

    private TestGitRepo(string rootPath, string barePath, string workPath, string mainSha)
    {
        RootPath = rootPath;
        BarePath = barePath;
        WorkPath = workPath;
        MainSha = mainSha;
        Url = new Uri(Path.GetFullPath(barePath)).AbsoluteUri;
    }

    /// <summary>
    /// 临时目录根路径。
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// 裸仓库路径，作为 git 远端。
    /// </summary>
    public string BarePath { get; }

    /// <summary>
    /// 工作副本路径，仅用于准备提交。
    /// </summary>
    public string WorkPath { get; }

    /// <summary>
    /// main 分支当前提交 SHA。
    /// </summary>
    public string MainSha { get; }

    /// <summary>
    /// 裸仓库的 file:// 地址，可直接交给被测代码。
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// 创建临时裸仓库、工作副本并推入一个 main 提交。
    /// </summary>
    /// <returns>已准备好的测试仓库</returns>
    public static async Task<TestGitRepo> CreateAsync()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            $"codesentinel-git-{Guid.NewGuid():N}");
        var barePath = Path.Combine(rootPath, "origin.git");
        var workPath = Path.Combine(rootPath, "work");
        Directory.CreateDirectory(rootPath);

        await RunGitAsync(rootPath, ["init", "--bare", barePath]);
        await RunGitAsync(rootPath, ["init", "-b", "main", workPath]);
        await RunGitAsync(workPath, ["config", "user.name", "CodeSentinel Test"]);
        await RunGitAsync(workPath, ["config", "user.email", "test@codesentinel.local"]);

        await File.WriteAllTextAsync(Path.Combine(workPath, "README.md"), "# test\n");
        await RunGitAsync(workPath, ["add", "README.md"]);
        await RunGitAsync(workPath, ["commit", "-m", "init"]);

        var url = new Uri(Path.GetFullPath(barePath)).AbsoluteUri;
        await RunGitAsync(workPath, ["remote", "add", "origin", url]);
        await RunGitAsync(workPath, ["push", "origin", "main"]);
        var mainSha = (await RunGitAsync(workPath, ["rev-parse", "HEAD"])).Trim();

        return new TestGitRepo(rootPath, barePath, workPath, mainSha);
    }

    /// <summary>
    /// 从 main 拉出新分支，提交若干次并推送到远端，随后切回 main。
    /// </summary>
    /// <param name="branchName">分支名</param>
    /// <param name="commitCount">新分支上的提交数量</param>
    /// <returns>新分支的提交 SHA</returns>
    public async Task<string> CreateBranchAsync(string branchName, int commitCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (commitCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(commitCount), "提交数量必须大于 0");
        }

        // 分支名中的斜杠不能直接作为文件名
        var filePrefix = branchName.Replace('/', '-');
        await RunGitAsync(WorkPath, ["checkout", "-b", branchName, "main"]);

        for (var index = 0; index < commitCount; index++)
        {
            var fileName = $"{filePrefix}-{index}-{Guid.NewGuid():N}.txt";
            await File.WriteAllTextAsync(Path.Combine(WorkPath, fileName), $"commit {index}\n");
            await RunGitAsync(WorkPath, ["add", fileName]);
            await RunGitAsync(WorkPath, ["commit", "-m", $"{branchName} commit {index}"]);
        }

        await RunGitAsync(WorkPath, ["push", "origin", branchName]);
        var sha = (await RunGitAsync(WorkPath, ["rev-parse", "HEAD"])).Trim();
        await RunGitAsync(WorkPath, ["checkout", "main"]);
        return sha;
    }

    /// <summary>
    /// 在指定分支追加一个提交并推送，随后切回 main，用于制造“分支有新提交”的场景。
    /// </summary>
    /// <param name="branchName">分支名</param>
    /// <returns>新提交 SHA</returns>
    public async Task<string> CommitOnBranchAsync(string branchName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        // 分支名中的斜杠不能直接作为文件名
        var filePrefix = branchName.Replace('/', '-');
        await RunGitAsync(WorkPath, ["checkout", branchName]);

        var fileName = $"{filePrefix}-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(WorkPath, fileName), "new commit\n");
        await RunGitAsync(WorkPath, ["add", fileName]);
        await RunGitAsync(WorkPath, ["commit", "-m", $"{branchName} new commit"]);
        await RunGitAsync(WorkPath, ["push", "origin", branchName]);

        var sha = (await RunGitAsync(WorkPath, ["rev-parse", "HEAD"])).Trim();
        await RunGitAsync(WorkPath, ["checkout", "main"]);
        return sha;
    }

    /// <summary>
    /// 在 main 上追加一个提交并推送，用于制造与旧分支分叉的场景。
    /// </summary>
    /// <returns>main 新的提交 SHA</returns>
    public async Task<string> CommitOnMainAsync()
    {
        await RunGitAsync(WorkPath, ["checkout", "main"]);
        var fileName = $"main-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(WorkPath, fileName), "main\n");
        await RunGitAsync(WorkPath, ["add", fileName]);
        await RunGitAsync(WorkPath, ["commit", "-m", "main commit"]);
        await RunGitAsync(WorkPath, ["push", "origin", "main"]);
        return (await RunGitAsync(WorkPath, ["rev-parse", "HEAD"])).Trim();
    }

    /// <summary>
    /// 从 main 拉出新分支并推送到远端，但不产生任何文件变更（空提交），供精确构造 diff 场景。
    /// </summary>
    /// <param name="branchName">分支名</param>
    /// <returns>新分支的提交 SHA</returns>
    public async Task<string> CreateBranchFromMainAsync(string branchName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        await RunGitAsync(WorkPath, ["checkout", "-b", branchName, "main"]);
        await RunGitAsync(WorkPath, ["commit", "--allow-empty", "-m", $"branch {branchName}"]);
        await RunGitAsync(WorkPath, ["push", "origin", branchName]);
        var sha = (await RunGitAsync(WorkPath, ["rev-parse", "HEAD"])).Trim();
        await RunGitAsync(WorkPath, ["checkout", "main"]);
        return sha;
    }

    /// <summary>
    /// 在指定分支上执行一组文件操作并提交推送：写入/覆盖文件、删除文件与重命名文件。
    /// </summary>
    /// <param name="branch">分支名</param>
    /// <param name="writes">相对路径到文件字节内容，创建或覆盖文件，支持中文路径与二进制内容</param>
    /// <param name="deletes">待删除的相对路径列表</param>
    /// <param name="renames">待重命名的旧路径到新路径列表</param>
    /// <param name="message">提交信息</param>
    /// <returns>提交后的分支 SHA</returns>
    public async Task<string> CommitChangesAsync(
        string branch,
        IReadOnlyDictionary<string, byte[]> writes,
        IReadOnlyList<string>? deletes = null,
        IReadOnlyList<(string OldPath, string NewPath)>? renames = null,
        string message = "changes")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        await RunGitAsync(WorkPath, ["checkout", branch]);

        foreach (var (path, content) in writes)
        {
            var fullPath = Path.Combine(WorkPath, path.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(fullPath, content);
        }

        foreach (var path in deletes ?? [])
        {
            await RunGitAsync(WorkPath, ["rm", "-q", "--", path]);
        }

        foreach (var (oldPath, newPath) in renames ?? [])
        {
            await RunGitAsync(WorkPath, ["mv", oldPath, newPath]);
        }

        await RunGitAsync(WorkPath, ["add", "-A"]);
        await RunGitAsync(WorkPath, ["commit", "-m", message]);
        await RunGitAsync(WorkPath, ["push", "origin", branch]);

        var sha = (await RunGitAsync(WorkPath, ["rev-parse", "HEAD"])).Trim();
        await RunGitAsync(WorkPath, ["checkout", "main"]);
        return sha;
    }

    /// <summary>
    /// 在指定工作目录执行 git 命令，失败时抛出包含标准错误的异常。
    /// </summary>
    /// <param name="workDir">工作目录</param>
    /// <param name="args">git 参数</param>
    /// <returns>标准输出</returns>
    private static async Task<string> RunGitAsync(string workDir, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(GitTimeoutMilliseconds);
        await process.WaitForExitAsync(timeout.Token);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} 执行失败（{process.ExitCode}）：{stdErr}");
        }

        return stdOut;
    }

    /// <summary>
    /// 删除临时目录，清理只读的 git 对象后重试。
    /// </summary>
    public void Dispose()
    {
        TestPaths.DeleteDirectory(RootPath);
    }
}

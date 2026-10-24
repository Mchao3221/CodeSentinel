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

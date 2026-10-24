using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace CodeSentinel.Git;

/// <summary>
/// git 命令行封装：逐参数传参、加载临时凭据脚本并安全清理。
/// </summary>
public sealed class GitCli
{
    /// <summary>
    /// 未配置超时时间时使用的默认值（秒）。
    /// </summary>
    private const int DefaultTimeoutSeconds = 60;

    /// <summary>
    /// 超时时返回的退出码。
    /// </summary>
    public const int TimeoutExitCode = -1;

    /// <summary>
    /// 超时提示，作为 GitResult.StdErr 返回。
    /// </summary>
    public const string TimeoutMessage = "git 命令执行超时";

    private readonly string _executable;
    private readonly TimeSpan _timeout;
    private readonly ILogger<GitCli> _logger;

    /// <summary>
    /// 初始化 git 命令封装。
    /// </summary>
    /// <param name="configuration">应用配置，读取 Git:ExecutablePath 与 Git:TimeoutSeconds</param>
    /// <param name="logger">日志记录器</param>
    public GitCli(IConfiguration configuration, ILogger<GitCli> logger)
    {
        _executable = configuration.GetValue("Git:ExecutablePath", "git") ?? "git";
        var timeoutSeconds = configuration.GetValue("Git:TimeoutSeconds", DefaultTimeoutSeconds);
        _timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds);
        _logger = logger;
    }

    /// <summary>
    /// 创建 askpass 脚本的工厂，默认写到系统临时目录；测试可替换以观察清理结果。
    /// </summary>
    public Func<AskPassScript> AskPassFactory { get; init; } = () => AskPassScript.Create();

    /// <summary>
    /// 验证 git 命令可用，供应用启动自检使用；可执行文件不存在时返回 false。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>git --version 是否执行成功</returns>
    public async Task<bool> VerifyAvailableAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunAsync(Path.GetTempPath(), ["--version"], null, ct);
            return result.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Win32Exception)
        {
            // 可执行文件不存在或无法启动
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process.Start 返回 false
            return false;
        }
    }

    /// <summary>
    /// 执行一条 git 命令并异步收集输出。
    /// </summary>
    /// <param name="workDir">工作目录，不存在时自动创建</param>
    /// <param name="args">git 参数，逐项传给 ProcessStartInfo.ArgumentList</param>
    /// <param name="credentials">只读凭据，为空时不注入 askpass</param>
    /// <param name="ct">取消令牌，取消时终止整个进程树</param>
    /// <returns>退出码与标准输出/错误</returns>
    public async Task<GitResult> RunAsync(
        string workDir,
        IReadOnlyList<string> args,
        GitCredentials? credentials,
        CancellationToken ct)
    {
        var (exitCode, stdOut, stdErr) = await RunCoreAsync(workDir, args, credentials, ct);
        return new GitResult(exitCode, Encoding.UTF8.GetString(stdOut), stdErr);
    }

    /// <summary>
    /// 执行一条 git 命令，标准输出按原始字节返回，供读取二进制 blob 使用。
    /// </summary>
    /// <param name="workDir">工作目录，不存在时自动创建</param>
    /// <param name="args">git 参数，逐项传给 ProcessStartInfo.ArgumentList</param>
    /// <param name="credentials">只读凭据，为空时不注入 askpass</param>
    /// <param name="ct">取消令牌，取消时终止整个进程树</param>
    /// <returns>退出码与原始标准输出/标准错误</returns>
    public async Task<GitRawResult> RunRawAsync(
        string workDir,
        IReadOnlyList<string> args,
        GitCredentials? credentials,
        CancellationToken ct)
    {
        var (exitCode, stdOut, stdErr) = await RunCoreAsync(workDir, args, credentials, ct);
        return new GitRawResult(exitCode, stdOut, stdErr);
    }

    /// <summary>
    /// 启动 git 进程并读取原始标准输出与文本标准错误，供 RunAsync 与 RunRawAsync 复用。
    /// </summary>
    /// <param name="workDir">工作目录</param>
    /// <param name="args">git 参数</param>
    /// <param name="credentials">只读凭据</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>退出码、原始标准输出与标准错误</returns>
    private async Task<(int ExitCode, byte[] StdOut, string StdErr)> RunCoreAsync(
        string workDir,
        IReadOnlyList<string> args,
        GitCredentials? credentials,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDir);
        ArgumentNullException.ThrowIfNull(args);
        Directory.CreateDirectory(workDir);

        using var script = credentials is null ? null : AskPassFactory();
        var startInfo = BuildStartInfo(workDir, args, credentials, script);
        _logger.LogDebug("执行 git：{Arguments}", DescribeArguments(args, credentials));

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 git 进程");
        }

        // 先异步读取管道，避免输出过多时子进程阻塞
        var stdOutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, CancellationToken.None);
        var stdErrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);

        if (!await WaitForExitAsync(process, stdOutTask, stdErrTask, ct, timeout.Token))
        {
            return (TimeoutExitCode, await stdOutTask, TimeoutMessage);
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;
        var exitCode = process.ExitCode;
        if (exitCode != 0)
        {
            _logger.LogWarning(
                "git 执行失败（退出码 {ExitCode}）：{Error}",
                exitCode,
                Redact(stdErr, credentials));
        }

        return (exitCode, stdOut, stdErr);
    }

    /// <summary>
    /// 等待进程结束；调用方取消时继续抛出，超时时终止进程树并返回 false。
    /// </summary>
    /// <param name="process">git 进程</param>
    /// <param name="stdOutTask">标准输出读取任务</param>
    /// <param name="stdErrTask">标准错误读取任务</param>
    /// <param name="ct">调用方取消令牌</param>
    /// <param name="timeoutToken">含超时的组合令牌</param>
    /// <returns>是否在超时前正常退出</returns>
    private static async Task<bool> WaitForExitAsync(
        Process process,
        Task stdOutTask,
        Task stdErrTask,
        CancellationToken ct,
        CancellationToken timeoutToken)
    {
        try
        {
            await process.WaitForExitAsync(timeoutToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            await DrainAsync(stdOutTask, stdErrTask);

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    /// <summary>
    /// 构造进程启动参数：逐参数传递、禁用系统配置与终端提示。
    /// </summary>
    /// <param name="workDir">工作目录</param>
    /// <param name="args">git 参数</param>
    /// <param name="credentials">只读凭据</param>
    /// <param name="script">askpass 脚本</param>
    /// <returns>进程启动信息</returns>
    private ProcessStartInfo BuildStartInfo(
        string workDir,
        IReadOnlyList<string> args,
        GitCredentials? credentials,
        AskPassScript? script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executable,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 中文路径按原文输出，避免被转义成八进制
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotePath=false");

        // 有凭据时禁用系统 credential helper，确保只使用本次临时 askpass
        if (credentials is not null)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("credential.helper=");
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        // 凭据只注入子进程环境变量，不进入命令行，也不会被日志记录
        if (credentials is not null && script is not null)
        {
            startInfo.Environment["GIT_ASKPASS"] = script.ScriptPath;
            startInfo.Environment["GIT_USERNAME"] = credentials.Username;
            startInfo.Environment["GIT_PASSWORD"] = credentials.Password;
        }

        return startInfo;
    }

    /// <summary>
    /// 终止整个进程树，超时或取消时使用。
    /// </summary>
    /// <param name="process">git 进程</param>
    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出
        }
        catch (Win32Exception)
        {
            // 进程无法终止时交给系统回收
        }
    }

    /// <summary>
    /// 等待管道读取结束，进程被终止后读取异常可忽略。
    /// </summary>
    /// <param name="stdOutTask">标准输出读取任务</param>
    /// <param name="stdErrTask">标准错误读取任务</param>
    private static async Task DrainAsync(Task stdOutTask, Task stdErrTask)
    {
        try
        {
            await Task.WhenAll(stdOutTask, stdErrTask);
        }
        catch (Exception)
        {
            // 进程被终止后管道读取可能抛出异常，不影响调用方判断
        }
    }

    /// <summary>
    /// 把流中的全部原始字节读入内存，供二进制标准输出使用。
    /// </summary>
    /// <param name="stream">输入流</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>原始字节</returns>
    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return memory.ToArray();
    }

    /// <summary>
    /// 生成可安全写入日志的参数描述，URL 与凭据中的敏感信息会被脱敏。
    /// </summary>
    /// <param name="args">git 参数</param>
    /// <param name="credentials">只读凭据，可为空</param>
    /// <returns>脱敏后的参数串</returns>
    private static string DescribeArguments(
        IReadOnlyList<string> args,
        GitCredentials? credentials)
    {
        return Redact(string.Join(' ', args), credentials);
    }

    /// <summary>
    /// 脱敏文本中的 URL 内嵌凭据与明文密码。
    /// </summary>
    /// <param name="value">原始文本</param>
    /// <param name="credentials">只读凭据，可为空</param>
    /// <returns>脱敏后的文本</returns>
    private static string Redact(string? value, GitCredentials? credentials)
    {
        var redacted = GitUrlRedactor.Redact(value) ?? string.Empty;
        if (credentials is not null && !string.IsNullOrEmpty(credentials.Password))
        {
            redacted = redacted.Replace(credentials.Password, "***", StringComparison.Ordinal);
        }

        return redacted;
    }
}

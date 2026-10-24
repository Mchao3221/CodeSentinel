namespace CodeSentinel.Git;

/// <summary>
/// git 命令执行结果。
/// </summary>
/// <param name="ExitCode">进程退出码</param>
/// <param name="StdOut">标准输出</param>
/// <param name="StdErr">标准错误</param>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// git 命令执行结果，标准输出按原始字节返回，供读取二进制 blob 使用。
/// </summary>
/// <param name="ExitCode">进程退出码</param>
/// <param name="StdOut">原始标准输出</param>
/// <param name="StdErr">标准错误</param>
public sealed record GitRawResult(int ExitCode, byte[] StdOut, string StdErr);

/// <summary>
/// 镜像中的远端分支信息。
/// </summary>
/// <param name="Name">去掉 origin/ 前缀的分支名</param>
/// <param name="HeadSha">分支当前提交 SHA</param>
public sealed record BranchInfo(string Name, string HeadSha);

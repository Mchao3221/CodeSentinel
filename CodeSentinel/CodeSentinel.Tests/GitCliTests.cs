using CodeSentinel.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeSentinel.Tests;

/// <summary>
/// git 命令封装与 URL 脱敏测试。
/// </summary>
public class GitCliTests
{
    /// <summary>
    /// URL 中内嵌的用户名密码必须被替换为固定脱敏标记。
    /// </summary>
    [Fact]
    public void Redacts_credentials_in_urls()
    {
        Assert.Equal("https://***@host/x", GitUrlRedactor.Redact("https://u:p@host/x"));
        Assert.Equal("http://***@h:8080/repo.git", GitUrlRedactor.Redact("http://token@h:8080/repo.git"));
        Assert.Equal("https://host/x", GitUrlRedactor.Redact("https://host/x"));
        Assert.Equal(
            "fatal: unable to access 'https://***@host/x': failed",
            GitUrlRedactor.Redact("fatal: unable to access 'https://u:p@host/x': failed"));
        Assert.Null(GitUrlRedactor.Redact(null));
        Assert.Equal(string.Empty, GitUrlRedactor.Redact(string.Empty));
    }

    /// <summary>
    /// 带凭据运行后，askpass 脚本与临时目录都必须被删除。
    /// </summary>
    [Fact]
    public async Task Askpass_script_is_deleted_after_run()
    {
        var askPassRoot = TestPaths.CreateTempDirectory("codesentinel-askpass-test");
        var workRoot = TestPaths.CreateTempDirectory("codesentinel-gitcli-work");
        AskPassScript? created = null;

        try
        {
            var git = new GitCli(CreateConfiguration(), NullLogger<GitCli>.Instance)
            {
                AskPassFactory = () => created = AskPassScript.Create(askPassRoot)
            };

            var result = await git.RunAsync(
                workRoot,
                ["--version"],
                new GitCredentials("ci-user", "super-secret-password"),
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("git version", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(created);
            Assert.False(File.Exists(created.ScriptPath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(created.ScriptPath)));
            Assert.Empty(Directory.GetFileSystemEntries(askPassRoot));
        }
        finally
        {
            TestPaths.DeleteDirectory(askPassRoot);
            TestPaths.DeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// 不带凭据时不应创建 askpass 脚本。
    /// </summary>
    [Fact]
    public async Task No_askpass_script_without_credentials()
    {
        var askPassRoot = TestPaths.CreateTempDirectory("codesentinel-askpass-test");
        var workRoot = TestPaths.CreateTempDirectory("codesentinel-gitcli-work");

        try
        {
            var git = new GitCli(CreateConfiguration(), NullLogger<GitCli>.Instance)
            {
                AskPassFactory = () => AskPassScript.Create(askPassRoot)
            };

            var result = await git.RunAsync(
                workRoot,
                ["--version"],
                null,
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.Empty(Directory.GetFileSystemEntries(askPassRoot));
        }
        finally
        {
            TestPaths.DeleteDirectory(askPassRoot);
            TestPaths.DeleteDirectory(workRoot);
        }
    }

    /// <summary>
    /// 构造只包含 git 可执行文件名的配置。
    /// </summary>
    /// <returns>配置对象</returns>
    private static IConfiguration CreateConfiguration()
    {
        return new ConfigurationBuilder().Build();
    }
}

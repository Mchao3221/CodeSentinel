using System.Text;

namespace CodeSentinel.Git;

/// <summary>
/// 临时 askpass 脚本：git 需要凭据时执行它，从子进程环境变量读取用户名与密码。
/// </summary>
public sealed class AskPassScript : IDisposable
{
    /// <summary>
    /// askpass 脚本内容：git 以提示语作为第一个参数，用户名与密码分别从环境变量读取。
    /// </summary>
    private const string ScriptContent =
        "#!/bin/sh\n" +
        "case \"$1\" in\n" +
        "  *sername*) printf '%s\\n' \"$GIT_USERNAME\" ;;\n" +
        "  *) printf '%s\\n' \"$GIT_PASSWORD\" ;;\n" +
        "esac\n";

    private const string ScriptFileName = "git-askpass.sh";

    private bool _disposed;

    /// <summary>
    /// 初始化 askpass 脚本句柄。
    /// </summary>
    /// <param name="directoryPath">脚本所在临时目录</param>
    /// <param name="scriptPath">脚本完整路径</param>
    private AskPassScript(string directoryPath, string scriptPath)
    {
        DirectoryPath = directoryPath;
        ScriptPath = scriptPath;
    }

    /// <summary>
    /// askpass 脚本完整路径，注入子进程的 GIT_ASKPASS。
    /// </summary>
    public string ScriptPath { get; }

    /// <summary>
    /// 脚本所在临时目录，释放时一并删除。
    /// </summary>
    public string DirectoryPath { get; }

    /// <summary>
    /// 在唯一临时子目录中创建 askpass 脚本，并尽量收紧权限。
    /// </summary>
    /// <param name="rootDirectory">可选根目录，未提供时使用系统临时目录</param>
    /// <returns>可释放的脚本句柄</returns>
    public static AskPassScript Create(string? rootDirectory = null)
    {
        var root = rootDirectory
            ?? Path.Combine(Path.GetTempPath(), "codesentinel-askpass");
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SetOwnerOnlyMode(directory);

        var scriptPath = Path.Combine(directory, ScriptFileName);
        File.WriteAllText(scriptPath, ScriptContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        SetOwnerOnlyMode(scriptPath);

        return new AskPassScript(directory, scriptPath);
    }

    /// <summary>
    /// 删除脚本与临时目录；重复释放安全。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryDeleteFile(ScriptPath);
        TryDeleteDirectory(DirectoryPath);
    }

    /// <summary>
    /// 设置仅当前用户可读写的权限，Windows 下不支持时忽略。
    /// </summary>
    /// <param name="path">文件或目录路径</param>
    private static void SetOwnerOnlyMode(string path)
    {
        // Unix 权限位只在类 Unix 平台可用，Windows 没有对应概念
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            return;
        }

        const UnixFileMode ownerOnly =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        try
        {
            File.SetUnixFileMode(path, ownerOnly);
        }
        catch (PlatformNotSupportedException)
        {
            // Windows 不支持 Unix 权限位，忽略
        }
        catch (IOException)
        {
            // 权限收紧失败不影响脚本创建
        }
        catch (UnauthorizedAccessException)
        {
            // 权限收紧失败不影响脚本创建
        }
    }

    /// <summary>
    /// 尽力删除脚本文件。
    /// </summary>
    /// <param name="path">脚本路径</param>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 临时文件删除失败时交给系统回收
        }
        catch (UnauthorizedAccessException)
        {
            // 临时文件删除失败时交给系统回收
        }
    }

    /// <summary>
    /// 尽力删除临时目录。
    /// </summary>
    /// <param name="path">目录路径</param>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (IOException)
        {
            // 临时目录删除失败时交给系统回收
        }
        catch (UnauthorizedAccessException)
        {
            // 临时目录删除失败时交给系统回收
        }
    }
}

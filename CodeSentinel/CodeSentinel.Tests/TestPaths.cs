namespace CodeSentinel.Tests;

/// <summary>
/// 测试临时目录工具：统一创建与清理，兼容 Windows 下 git 对象的只读属性。
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// 在系统临时目录下创建唯一目录。
    /// </summary>
    /// <param name="prefix">目录名前缀</param>
    /// <returns>目录绝对路径</returns>
    public static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// 删除目录，先清除只读属性并重试，尽力避免 git 句柄延迟释放导致的失败。
    /// </summary>
    /// <param name="path">目录路径</param>
    public static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                ClearReadOnlyAttributes(path);
                Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                // 清理失败时忽略，临时目录由系统回收
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // 清理失败时忽略，临时目录由系统回收
                return;
            }
        }
    }

    /// <summary>
    /// 递归清除只读属性，避免 Windows 下无法删除 git 对象。
    /// </summary>
    /// <param name="directory">目录路径</param>
    private static void ClearReadOnlyAttributes(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }
}

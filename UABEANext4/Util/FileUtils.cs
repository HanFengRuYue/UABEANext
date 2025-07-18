using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UABEANext4.Logic;

namespace UABEANext4.Util;

public static class FileUtils
{
    private static readonly string[] BYTE_SIZE_SUFFIXES = new string[] { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
    public static string GetFormattedByteSize(long size)
    {
        int log = (int)Math.Log(size, 1024);
        double div = log == 0 ? 1 : Math.Pow(1024, log);
        double num = size / div;
        return $"{num:f2}{BYTE_SIZE_SUFFIXES[log]}";
    }

    public static List<string> GetFilesInDirectory(string path, List<string> extensions)
    {
        List<string> files = new List<string>();
        foreach (string extension in extensions)
        {
            files.AddRange(Directory.GetFiles(path, "*." + extension));
        }
        return files;
    }

    /// <summary>
    /// 扫描Unity游戏目录，查找可处理的文件
    /// </summary>
    /// <param name="directoryPath">游戏目录路径</param>
    /// <param name="progressCallback">进度回调函数</param>
    /// <param name="options">扫描选项</param>
    /// <returns>可处理的文件列表</returns>
    public static List<string> ScanUnityGameDirectory(string directoryPath, Action<string>? progressCallback = null, ScanOptions? options = null)
    {
        var supportedFiles = new List<string>();
        
        if (!Directory.Exists(directoryPath))
        {
            return supportedFiles;
        }

        // 使用默认选项
        options ??= new ScanOptions();

        // 定义Unity文件扩展名
        var unityExtensions = new[] { ".assets", ".resS", ".resource", ".bundle" };
        
        try
        {
            progressCallback?.Invoke("正在扫描文件...");
            
            // 确定要扫描的目录
            var directoriesToScan = new List<string> { directoryPath };
            
            if (options.ScanCommonUnityDirectories)
            {
                var commonDirs = GetCommonUnityDirectories(directoryPath);
                directoriesToScan.AddRange(commonDirs);
            }
            
            // 扫描所有文件
            var allFiles = new List<string>();
            foreach (var dir in directoriesToScan)
            {
                var searchOption = options.IncludeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                var files = Directory.GetFiles(dir, "*.*", searchOption)
                    .Where(file => 
                    {
                        var extension = Path.GetExtension(file).ToLowerInvariant();
                        return unityExtensions.Contains(extension);
                    });
                allFiles.AddRange(files);
            }
            
            // 去重
            allFiles = allFiles.Distinct().ToList();

            progressCallback?.Invoke($"找到 {allFiles.Count} 个Unity文件，正在验证...");

            // 进一步筛选：尝试检测文件类型
            int processedCount = 0;
            foreach (var file in allFiles)
            {
                try
                {
                    processedCount++;
                    if (processedCount % 10 == 0) // 每处理10个文件更新一次进度
                    {
                        progressCallback?.Invoke($"正在验证文件... ({processedCount}/{allFiles.Count})");
                    }

                    // 检查文件大小，跳过太小的文件
                    if (options.SkipSmallFiles)
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.Length < 0x20) // 小于32字节的文件通常不是有效的Unity文件
                        {
                            continue;
                        }
                    }

                    // 尝试检测文件类型
                    if (options.ValidateFileTypes)
                    {
                        var detectedType = FileTypeDetector.DetectFileType(file);
                        if (detectedType != DetectedFileType.Unknown)
                        {
                            supportedFiles.Add(file);
                        }
                        else if (file.EndsWith(".resS") || file.EndsWith(".resource"))
                        {
                            // 资源文件即使检测失败也添加，因为它们是Unity的标准资源文件
                            supportedFiles.Add(file);
                        }
                    }
                    else
                    {
                        // 不验证文件类型，直接添加
                        supportedFiles.Add(file);
                    }
                }
                catch
                {
                    // 忽略无法读取的文件
                    continue;
                }
            }

            progressCallback?.Invoke($"验证完成，找到 {supportedFiles.Count} 个可处理的文件");
        }
        catch (Exception)
        {
            // 忽略目录访问错误
            progressCallback?.Invoke("扫描过程中出现错误");
        }

        return supportedFiles;
    }

    /// <summary>
    /// 获取Unity游戏目录的常见子目录
    /// </summary>
    /// <param name="directoryPath">游戏目录路径</param>
    /// <returns>常见Unity目录列表</returns>
    public static List<string> GetCommonUnityDirectories(string directoryPath)
    {
        var commonDirs = new List<string>();
        
        if (!Directory.Exists(directoryPath))
        {
            return commonDirs;
        }

        var unityDirs = new[]
        {
            "Data",
            "StreamingAssets", 
            "Managed",
            "Plugins",
            "Resources",
            "AssetBundles"
        };

        foreach (var dir in unityDirs)
        {
            var fullPath = Path.Combine(directoryPath, dir);
            if (Directory.Exists(fullPath))
            {
                commonDirs.Add(fullPath);
            }
        }

        return commonDirs;
    }
}

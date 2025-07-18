using AssetsTools.NET;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Linq;

namespace UABEANext4.Util;

public static class FileTypeDetector
{
    private static readonly ConcurrentDictionary<string, CachedFileInfo> _cache = new();
    private const int MAX_CACHE_SIZE = 1000; // 限制缓存大小
    private static readonly object _cacheLock = new object();

    private struct CachedFileInfo
    {
        public DetectedFileType FileType;
        public long FileSize;
        public DateTime LastWriteTime;
    }

    public static DetectedFileType DetectFileType(string filePath)
    {
        try
        {
            // 获取文件信息
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                return DetectedFileType.Unknown;
            }

            // 检查缓存
            var cacheKey = Path.GetFullPath(filePath);
            if (_cache.TryGetValue(cacheKey, out var cachedInfo))
            {
                // 检查文件是否已更改
                if (cachedInfo.FileSize == fileInfo.Length && 
                    cachedInfo.LastWriteTime == fileInfo.LastWriteTime)
                {
                    return cachedInfo.FileType;
                }
            }

            // 缓存未命中或文件已更改，重新检测
            DetectedFileType detectedType;
            using (FileStream fs = File.OpenRead(filePath))
            using (AssetsFileReader r = new AssetsFileReader(fs))
            {
                detectedType = DetectFileType(r, 0);
            }

            // 更新缓存
            var newCacheInfo = new CachedFileInfo
            {
                FileType = detectedType,
                FileSize = fileInfo.Length,
                LastWriteTime = fileInfo.LastWriteTime
            };

            lock (_cacheLock)
            {
                // 如果缓存过大，清理一些旧条目
                if (_cache.Count >= MAX_CACHE_SIZE)
                {
                    var oldestKeys = _cache.Take(MAX_CACHE_SIZE / 4).Select(kvp => kvp.Key).ToList();
                    foreach (var key in oldestKeys)
                    {
                        _cache.TryRemove(key, out _);
                    }
                }

                _cache[cacheKey] = newCacheInfo;
            }

            return detectedType;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error detecting file type for {filePath}: {ex.Message}");
            return DetectedFileType.Unknown;
        }
    }

    /// <summary>
    /// 清空文件类型检测缓存
    /// </summary>
    public static void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            System.Diagnostics.Debug.WriteLine("File type detection cache cleared");
        }
    }

    /// <summary>
    /// 获取缓存统计信息
    /// </summary>
    public static (int Count, int MaxSize) GetCacheStats()
    {
        return (_cache.Count, MAX_CACHE_SIZE);
    }

    public static DetectedFileType DetectFileType(AssetsFileReader r, long startAddress)
    {
        string possibleBundleHeader;
        int possibleFormat;
        string emptyVersion, fullVersion;

        r.BigEndian = true;

        if (r.BaseStream.Length < 0x20)
        {
            return DetectedFileType.Unknown;
        }
        r.Position = startAddress;
        possibleBundleHeader = r.ReadStringLength(7);
        r.Position = startAddress + 0x08;
        possibleFormat = r.ReadInt32();

        r.Position = startAddress + (possibleFormat >= 0x16 ? 0x30 : 0x14);

        string possibleVersion = "";
        char curChar;
        while (r.Position < r.BaseStream.Length && (curChar = (char)r.ReadByte()) != 0x00)
        {
            possibleVersion += curChar;
            if (possibleVersion.Length > 0xFF)
            {
                break;
            }
        }
        emptyVersion = Regex.Replace(possibleVersion, "[a-zA-Z0-9\\.\\n]", "");
        fullVersion = Regex.Replace(possibleVersion, "[^a-zA-Z0-9\\.\\n]", "");

        if (possibleBundleHeader == "UnityFS")
        {
            return DetectedFileType.BundleFile;
        }
        else if (possibleFormat < 0xFF && emptyVersion.Length == 0 && fullVersion.Length >= 5)
        {
            return DetectedFileType.AssetsFile;
        }
        return DetectedFileType.Unknown;
    }
}

public enum DetectedFileType
{
    Unknown,
    AssetsFile,
    BundleFile
}

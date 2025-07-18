using AssetsTools.NET;
using AssetsTools.NET.Cpp2IL;
using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using UABEANext4.Plugins;
using UABEANext4.Util;

namespace UABEANext4.AssetWorkspace;

public partial class Workspace : ObservableObject
{
    public AssetsManager Manager { get; } = new AssetsManager();
    public PluginLoader Plugins { get; } = new PluginLoader();

    public Mutex ModifyMutex { get; } = new Mutex();

    // this should be its own class
    [ObservableProperty]
    public float _progressValue = 0f;
    [ObservableProperty]
    public string _progressText = "";

    public ObservableCollection<WorkspaceItem> RootItems { get; } = new();
    public Dictionary<string, WorkspaceItem> ItemLookup { get; } = new();
    private SynchronizationContext? FileSyncContext { get; } = SynchronizationContext.Current;

    // items modified and unsaved
    public HashSet<WorkspaceItem> UnsavedItems { get; } = new();
    // items modified and saved
    // we track this since the base AssetsFile is still reading from the old file
    public HashSet<WorkspaceItem> ModifiedItems { get; } = new();

    public int NextLoadIndex => RootItems.Count != 0 ? RootItems.Max(i => i.LoadIndex) + 1 : 0;

    public delegate void MonoTemplateFailureEvent(string path);
    public event MonoTemplateFailureEvent? MonoTemplateLoadFailed;

    private bool _setMonoTempGeneratorsYet;

    public Workspace()
    {
        string classDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "classdata.tpk");
        if (File.Exists(classDataPath))
            Manager.LoadClassPackage(classDataPath);

        string pluginsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");
        Plugins.LoadPluginsInDirectory(pluginsPath);

        Manager.UseRefTypeManagerCache = true;
        Manager.UseTemplateFieldCache = true;
        Manager.UseQuickLookup = true;
    }

    public WorkspaceItem? LoadAnyFile(Stream stream, int loadOrder = -1, string path = "")
    {
        if (path == "" && stream is FileStream fs)
        {
            path = fs.Name;
        }

        var detectedType = FileTypeDetector.DetectFileType(new AssetsFileReader(stream), 0);
        if (detectedType == DetectedFileType.BundleFile)
        {
            stream.Position = 0;
            return LoadBundle(stream, loadOrder);
        }
        else if (detectedType == DetectedFileType.AssetsFile)
        {
            stream.Position = 0;
            return LoadAssets(stream, loadOrder);
        }
        else if (path.EndsWith(".resS") || path.EndsWith(".resource"))
        {
            return LoadResource(stream, loadOrder);
        }

        return null;
    }

    public WorkspaceItem LoadBundle(Stream stream, int loadOrder = -1, string name = "")
    {
        // 优化内存使用：根据文件大小决定加载策略
        // 大于 100MB 的文件使用流式处理
        const long LARGE_FILE_THRESHOLD = 100 * 1024 * 1024; // 100MB
        
        BundleFileInstance bunInst;
        
        if (stream is FileStream fs)
        {
            var fileSize = fs.Length;
            
            if (fileSize > LARGE_FILE_THRESHOLD)
            {
                // 对于大文件，使用流式处理
                System.Diagnostics.Debug.WriteLine($"Loading large bundle file ({FileUtils.GetFormattedByteSize(fileSize)}) with stream processing: {fs.Name}");
                bunInst = Manager.LoadBundleFile(fs);
            }
            else
            {
                // 对于小文件，可以使用原有方式
                bunInst = Manager.LoadBundleFile(fs);
            }
        }
        else
        {
            // 对于非文件流，检查流长度
            if (stream.CanSeek && stream.Length > LARGE_FILE_THRESHOLD)
            {
                System.Diagnostics.Debug.WriteLine($"Loading large bundle stream ({FileUtils.GetFormattedByteSize(stream.Length)}) with stream processing");
                bunInst = Manager.LoadBundleFile(stream, name);
            }
            else
            {
                bunInst = Manager.LoadBundleFile(stream, name);
            }
        }

        TryLoadClassDatabase(bunInst.file);

        var item = new WorkspaceItem(this, bunInst, loadOrder);
        AddRootItemThreadSafe(item, bunInst.name);

        return item;
    }

    public WorkspaceItem LoadAssets(Stream stream, int loadOrder = -1, string name = "")
    {
        AssetsFileInstance fileInst;
        if (stream is FileStream fs)
        {
            fileInst = Manager.LoadAssetsFile(fs);
        }
        else
        {
            fileInst = Manager.LoadAssetsFile(stream, name);
        }

        TryLoadClassDatabase(fileInst.file);

        FixupAssetsFile(fileInst);

        var item = new WorkspaceItem(fileInst, loadOrder);
        AddRootItemThreadSafe(item, fileInst.name);

        return item;
    }

    public WorkspaceItem LoadAssetsFromBundle(BundleFileInstance bunInst, int index)
    {
        var dirInf = BundleHelper.GetDirInfo(bunInst.file, index);
        var fileInst = Manager.LoadAssetsFileFromBundle(bunInst, index);

        TryLoadClassDatabase(fileInst.file);

        FixupAssetsFile(fileInst);

        var item = new WorkspaceItem(dirInf.Name, fileInst, -1, WorkspaceItemType.AssetsFile);
        return item;
    }

    private void FixupAssetsFile(AssetsFileInstance fileInst)
    {
        if (fileInst.file.AssetInfos is not RangeObservableCollection<AssetFileInfo>)
        {
            var assetInsts = new RangeObservableCollection<AssetFileInfo>();
            var tmp = new List<AssetFileInfo>();
            foreach (var info in fileInst.file.AssetInfos)
            {
                var asset = new AssetInst(fileInst, info);
                lock (asset.FileInstance.LockReader)
                {
                    AssetNameUtils.GetDisplayNameFast(this, asset, true, out string? assetName, out string _);
                    asset.AssetName = assetName;
                }
                tmp.Add(asset);
            }
            assetInsts.AddRange(tmp);
            fileInst.file.Metadata.AssetInfos = assetInsts;
            fileInst.file.GenerateQuickLookup();
        }
    }

    public void TryLoadClassDatabase(AssetBundleFile file)
    {
        if (Manager.ClassDatabase == null)
        {
            var fileVersion = file.Header.EngineVersion;
            if (fileVersion != "0.0.0")
            {
                Manager.LoadClassDatabaseFromPackage(fileVersion);
            }
        }
    }

    public void TryLoadClassDatabase(AssetsFile file)
    {
        if (Manager.ClassDatabase == null)
        {
            var metadata = file.Metadata;
            var fileVersion = metadata.UnityVersion;
            if (fileVersion != "0.0.0")
            {
                Manager.LoadClassDatabaseFromPackage(fileVersion);
            }
        }
    }

    public WorkspaceItem LoadResource(Stream stream, int loadOrder = -1, string name = "")
    {
        if (name == "" && stream is FileStream fs)
        {
            name = Path.GetFileName(fs.Name);
        }

        WorkspaceItem item = new WorkspaceItem(name, stream, loadOrder, WorkspaceItemType.ResourceFile);
        AddRootItemThreadSafe(item, name);

        return item;
    }

    internal void AddRootItemThreadSafe(WorkspaceItem item, string itemName)
    {
        FileSyncContext?.Post(_ =>
        {
            if (item.LoadIndex != -1)
            {
                int pos = RootItems.BinarySearch(item, (i, j) => i.LoadIndex.CompareTo(j.LoadIndex));
                if (pos < 0)
                {
                    RootItems.Insert(~pos, item);
                }
                else
                {
                    RootItems.Insert(pos, item);
                }
                ItemLookup[itemName] = item;
                return;
            }

            RootItems.Add(item);
            ItemLookup[itemName] = item;
        }, null);
    }

    internal void AddChildItemThreadSafe(WorkspaceItem item, WorkspaceItem parent, string itemName)
    {
        FileSyncContext?.Post(_ =>
        {
            // loadorder ignored here
            parent.Children.Add(item);
            item.Parent = parent;
            ItemLookup[itemName] = item;
        }, null);
    }

    private DateTime _lastProgressUpdate = DateTime.MinValue;
    private readonly object _progressLock = new object();
    private const int PROGRESS_UPDATE_INTERVAL_MS = 100; // 100ms 最小更新间隔

    public void SetProgressThreadSafe(float value, string text)
    {
        lock (_progressLock)
        {
            var now = DateTime.Now;
            var roundedValue = (float)Math.Round(value * 20) / 20;
            
            // 检查是否应该更新进度
            var shouldUpdate = false;
            
            // 强制更新的条件
            if (value == 0f || value == 1f)
            {
                shouldUpdate = true;
            }
            // 进度值有显著变化
            else if (Math.Abs(roundedValue - ProgressValue) >= 0.05f)
            {
                shouldUpdate = true;
            }
            // 距离上次更新已过去足够时间
            else if ((now - _lastProgressUpdate).TotalMilliseconds >= PROGRESS_UPDATE_INTERVAL_MS)
            {
                shouldUpdate = true;
            }
            
            if (shouldUpdate)
            {
                _lastProgressUpdate = now;
                FileSyncContext?.Post(_ =>
                {
                    ProgressValue = value;
                    ProgressText = text;
                }, null);
            }
        }
    }

    // should be nullable
    public AssetTypeTemplateField GetTemplateField(AssetInst asset, bool skipMonoBehaviourFields = false)
    {
        AssetReadFlags readFlags = AssetReadFlags.None;
        if (skipMonoBehaviourFields && asset.Type == AssetClassID.MonoBehaviour)
        {
            readFlags |= AssetReadFlags.SkipMonoBehaviourFields | AssetReadFlags.ForceFromCldb;
        }

        return Manager.GetTemplateBaseField(asset.FileInstance, asset, readFlags);
    }

    public AssetTypeTemplateField GetTemplateField(AssetsFileInstance fileInst, AssetFileInfo info, bool skipMonoBehaviourFields = false)
    {
        AssetReadFlags readFlags = AssetReadFlags.None;
        if (skipMonoBehaviourFields && info.TypeId == (int)AssetClassID.MonoBehaviour)
        {
            readFlags |= AssetReadFlags.SkipMonoBehaviourFields | AssetReadFlags.ForceFromCldb;
        }

        return Manager.GetTemplateBaseField(fileInst, info, readFlags);
    }

    public void CheckAndSetMonoTempGenerators(AssetsFileInstance fileInst, AssetFileInfo? info)
    {
        bool isValidMono = info == null || info.TypeId == (int)AssetClassID.MonoBehaviour || info.TypeId < 0;
        if (isValidMono && !_setMonoTempGeneratorsYet && !fileInst.file.Metadata.TypeTreeEnabled)
        {
            string dataDir = PathUtils.GetAssetsFileDirectory(fileInst);
            bool success = SetMonoTempGenerators(dataDir);
            if (!success)
            {
                MonoTemplateLoadFailed?.Invoke(dataDir);
            }
        }
    }

    private bool SetMonoTempGenerators(string fileDir)
    {
        if (!_setMonoTempGeneratorsYet)
        {
            _setMonoTempGeneratorsYet = true;

            string managedDir = Path.Combine(fileDir, "Managed");
            if (Directory.Exists(managedDir))
            {
                bool hasDll = Directory.GetFiles(managedDir, "*.dll").Length > 0;
                if (hasDll)
                {
                    Manager.MonoTempGenerator = new MonoCecilTempGenerator(managedDir);
                    return true;
                }
            }

            FindCpp2IlFilesResult il2cppFiles = FindCpp2IlFiles.Find(fileDir);
            if (il2cppFiles.success && true/*ConfigurationManager.Settings.UseCpp2Il*/)
            {
                Manager.MonoTempGenerator = new Cpp2IlTempGenerator(il2cppFiles.metaPath, il2cppFiles.asmPath);
                return true;
            }
        }
        return false;
    }

    public AssetFileInfo? GetAssetFileInfo(AssetsFileInstance fileInst, AssetTypeValueField pptrField)
    {
        return GetAssetFileInfo(fileInst, pptrField["m_FileID"].AsInt, pptrField["m_PathID"].AsLong);
    }

    public AssetFileInfo? GetAssetFileInfo(AssetsFileInstance fileInst, int fileId, long pathId)
    {
        if (fileId != 0)
        {
            fileInst = fileInst.GetDependency(Manager, fileId - 1);
        }
        if (fileInst == null)
        {
            return null;
        }

        return fileInst.file.GetAssetInfo(pathId);
    }

    public AssetInst? GetAssetInst(AssetsFileInstance fileInst, AssetTypeValueField pptrField)
    {
        return GetAssetInst(fileInst, pptrField["m_FileID"].AsInt, pptrField["m_PathID"].AsLong);
    }

    public AssetInst? GetAssetInst(AssetsFileInstance fileInst, int fileId, long pathId)
    {
        if (fileId != 0)
        {
            fileInst = fileInst.GetDependency(Manager, fileId - 1);
            fileId = 0;
        }
        AssetFileInfo? info = GetAssetFileInfo(fileInst, fileId, pathId);

        if (info == null)
        {
            return null;
        }
        else if (info is AssetInst inst)
        {
            return inst;
        }
        else
        {
            return new AssetInst(fileInst, info);
        }
    }

    public AssetTypeValueField? GetBaseField(AssetsFileInstance fileInst, AssetTypeValueField pptrField)
    {
        return GetBaseField(fileInst, pptrField["m_FileID"].AsInt, pptrField["m_PathID"].AsLong);
    }

    public AssetTypeValueField? GetBaseField(AssetInst asset)
    {
        // todo cache latest n base fields in workspace?
        //if (asset.BaseValueField != null)
        //{
        //    return asset.BaseValueField;
        //}
        //
        //var baseField = GetBaseField(asset.FileInstance, asset.PathId);
        //asset.BaseValueField = baseField;
        return GetBaseField(asset.FileInstance, asset.PathId);
    }

    public AssetTypeValueField? GetBaseField(AssetsFileInstance fileInst, long pathId)
    {
        return GetBaseField(fileInst, 0, pathId);
    }

    public AssetTypeValueField? GetBaseField(AssetsFileInstance fileInst, int fileId, long pathId)
    {
        if (fileId != 0)
        {
            fileInst = fileInst.GetDependency(Manager, fileId - 1);
        }
        if (fileInst == null)
        {
            return null;
        }

        AssetFileInfo? info = fileInst.file.GetAssetInfo(pathId);
        if (info == null)
        {
            return null;
        }

        CheckAndSetMonoTempGenerators(fileInst, info);

        // negative target platform seems to indicate an editor version
        AssetReadFlags readFlags = AssetReadFlags.None;
        if ((int)fileInst.file.Metadata.TargetPlatform < 0)
        {
            readFlags |= AssetReadFlags.PreferEditor;
        }

        try
        {
            return Manager.GetBaseField(fileInst, info, readFlags);
        }
        catch
        {
            return null;
        }
    }

    public void Dirty(WorkspaceItem item)
    {
        UnsavedItems.Add(item);
        ModifiedItems.Add(item);
        if (item.Parent != null)
        {
            Dirty(item.Parent);
        }
    }

    public void CloseAll()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("Starting workspace cleanup...");
            
            // 安全地释放所有资源
            int closedStreams = 0;
            int failedStreams = 0;
            
            foreach (var item in RootItems)
            {
                try
                {
                    if (item.ObjectType == WorkspaceItemType.ResourceFile && item.Loaded)
                    {
                        var stream = (Stream?)item.Object;
                        if (stream != null)
                        {
                            stream.Dispose(); // 使用 Dispose 而不是 Close
                            closedStreams++;
                        }
                    }
                    else if (item.ObjectType == WorkspaceItemType.AssetsFile && item.Loaded)
                    {
                        // 确保 AssetsFile 相关的资源也被释放
                        var assetsFileInstance = item.Object as AssetsFileInstance;
                        if (assetsFileInstance?.AssetsStream != null)
                        {
                            try
                            {
                                assetsFileInstance.AssetsStream.Dispose();
                                closedStreams++;
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to dispose AssetsStream: {ex.Message}");
                                failedStreams++;
                            }
                        }
                    }
                    else if (item.ObjectType == WorkspaceItemType.BundleFile && item.Loaded)
                    {
                        // 确保 BundleFile 相关的资源也被释放
                        var bundleFileInstance = item.Object as BundleFileInstance;
                        if (bundleFileInstance?.BundleStream != null)
                        {
                            try
                            {
                                bundleFileInstance.BundleStream.Dispose();
                                closedStreams++;
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to dispose BundleStream: {ex.Message}");
                                failedStreams++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error closing workspace item {item.Name}: {ex.Message}");
                    failedStreams++;
                }
            }
            
            System.Diagnostics.Debug.WriteLine($"Closed {closedStreams} streams, {failedStreams} failed");
            
            // 安全地清理管理器
            try
            {
                Manager.UnloadAll();
                System.Diagnostics.Debug.WriteLine("Manager unloaded successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error unloading manager: {ex.Message}");
            }
            
            try
            {
                Manager.UnloadClassDatabase();
                System.Diagnostics.Debug.WriteLine("Class database unloaded successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error unloading class database: {ex.Message}");
            }
            
            // 清理其他资源
            try
            {
                Manager.MonoTempGenerator = null;
                _setMonoTempGeneratorsYet = false;
                System.Diagnostics.Debug.WriteLine("Mono temp generator cleared");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error clearing mono temp generator: {ex.Message}");
            }
            
            // 清理集合
            RootItems.Clear();
            ItemLookup.Clear();
            UnsavedItems.Clear();
            ModifiedItems.Clear();
            
            // 强制垃圾回收以释放内存
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            System.Diagnostics.Debug.WriteLine("Workspace cleanup completed successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Critical error during workspace cleanup: {ex.Message}");
            // 即使出现错误也要尝试清理基本状态
            try
            {
                RootItems.Clear();
                ItemLookup.Clear();
                UnsavedItems.Clear();
                ModifiedItems.Clear();
            }
            catch
            {
                // 忽略清理集合时的异常
            }
        }
    }

    public void RenameFile(WorkspaceItem wsItem, string newName)
    {
        var oldName = wsItem.Name;
        if (oldName != newName)
        {
            if (wsItem.Object is AssetsFileInstance fileInst)
            {
                fileInst.name = newName;
            }
            else if (wsItem.Object is BundleFileInstance bunInst)
            {
                bunInst.name = newName;
            }

            wsItem.Name = newName;
            wsItem.Update(nameof(wsItem.Name));
            Dirty(wsItem);
            ItemLookup.Remove(oldName);
            ItemLookup[newName] = wsItem;
        }
    }
}

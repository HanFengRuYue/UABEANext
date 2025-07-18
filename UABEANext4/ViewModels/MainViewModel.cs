using AssetsTools.NET.Extra;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UABEANext4.AssetWorkspace;
using UABEANext4.Logic;
using UABEANext4.Services;
using UABEANext4.Util;
using UABEANext4.ViewModels.Dialogs;
using UABEANext4.ViewModels.Documents;
using UABEANext4.ViewModels.Tools;

namespace UABEANext4.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    public IRootDock? _layout;

    public Workspace Workspace { get; }

    public bool UsesChrome => OperatingSystem.IsWindows();

    public ExtendClientAreaChromeHints ChromeHints => UsesChrome
        ? ExtendClientAreaChromeHints.PreferSystemChrome
        : ExtendClientAreaChromeHints.Default;

    private readonly MainDockFactory _factory;
    private List<AssetsFileInstance> _lastLoadedFiles = new();

    public MainViewModel()
    {
        Workspace = new();
        _factory = new MainDockFactory(Workspace);
        Layout = _factory.CreateLayout();
        if (Layout is not null)
        {
            _factory.InitLayout(Layout);
        }

        WeakReferenceMessenger.Default.Register<SelectedWorkspaceItemChangedMessage>(this, (r, h) => _ = OnSelectedWorkspaceItemsChanged(r, h));
        WeakReferenceMessenger.Default.Register<RequestEditAssetMessage>(this, OnRequestEditAsset);
    }

    private int GetOptimalParallelism(int fileCount)
    {
        try
        {
            // 获取系统信息
            var processorCount = Environment.ProcessorCount;
            var totalMemory = GC.GetTotalMemory(false);
            var availableMemory = GetAvailableMemory();
            
            // 基础并行度：使用 CPU 核心数的一半到全部，但至少为 2
            int baseConcurrency = Math.Max(2, processorCount / 2);
            
            // 根据文件数量调整
            if (fileCount < 10)
            {
                // 文件很少，使用较低的并行度
                baseConcurrency = Math.Min(baseConcurrency, 2);
            }
            else if (fileCount > 100)
            {
                // 文件很多，可以使用更高的并行度
                baseConcurrency = Math.Min(processorCount, 8);
            }
            
            // 根据可用内存调整
            var memoryInGB = availableMemory / (1024 * 1024 * 1024);
            if (memoryInGB < 2)
            {
                // 内存不足，降低并行度
                baseConcurrency = Math.Min(baseConcurrency, 2);
            }
            else if (memoryInGB > 8)
            {
                // 内存充足，可以使用更高的并行度
                baseConcurrency = Math.Min(baseConcurrency * 2, processorCount);
            }
            
            // 最终限制：不超过处理器核心数，不少于 1
            int finalConcurrency = Math.Max(1, Math.Min(baseConcurrency, processorCount));
            
            System.Diagnostics.Debug.WriteLine($"File loading parallelism: {finalConcurrency} " +
                $"(CPU cores: {processorCount}, Files: {fileCount}, Memory: {memoryInGB:F1}GB)");
            
            return finalConcurrency;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error calculating optimal parallelism: {ex.Message}");
            // 出错时使用安全的默认值
            return Math.Min(Environment.ProcessorCount - 1, 4);
        }
    }

    private long GetAvailableMemory()
    {
        try
        {
            // 尝试获取可用物理内存
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var workingSet = process.WorkingSet64;
            
            // 简单估算：假设系统总内存的 70% 可用
            // 这是一个粗略的估计，实际可用内存可能不同
            return Math.Max(2L * 1024 * 1024 * 1024, workingSet * 4); // 至少假设 2GB
        }
        catch
        {
            // 如果无法获取内存信息，假设有 4GB 可用
            return 4L * 1024 * 1024 * 1024;
        }
    }

    public async Task OpenFiles(IEnumerable<string?> enumerable)
    {
        int totalCount = enumerable.Count();
        if (totalCount == 0)
        {
            return;
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = GetOptimalParallelism(totalCount)
        };

        await Task.Run(() =>
        {
            Workspace.ModifyMutex.WaitOne();
            Workspace.ProgressValue = 0;
            Workspace.ProgressText = "开始加载文件...";
            int startLoadOrder = Workspace.NextLoadIndex;
            int currentCount = 0;
            int successCount = 0;
            int skipCount = 0;
            
            Parallel.ForEach(enumerable, options, (fileName, state, index) =>
            {
                if (fileName is not null)
                {
                    FileStream? fileStream = null;
                    try
                    {
                        fileStream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var file = Workspace.LoadAnyFile(fileStream, startLoadOrder + (int)index);
                        
                        // 如果加载成功，文件流现在由 Workspace 管理
                        if (file != null)
                        {
                            fileStream = null; // 防止在 finally 块中关闭
                            var currentCountNow = Interlocked.Increment(ref currentCount);
                            var successCountNow = Interlocked.Increment(ref successCount);
                            var currentProgress = currentCountNow / (float)totalCount;
                            Workspace.SetProgressThreadSafe(currentProgress, $"已加载 {successCountNow}/{totalCount} - {Path.GetFileName(fileName)}");
                        }
                        else
                        {
                            // 加载失败，需要关闭文件流
                            throw new InvalidOperationException("文件加载失败");
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        var currentCountNow = Interlocked.Increment(ref currentCount);
                        var skipCountNow = Interlocked.Increment(ref skipCount);
                        var currentProgress = currentCountNow / (float)totalCount;
                        Workspace.SetProgressThreadSafe(currentProgress, $"跳过 {skipCountNow} 个文件 - 文件不存在: {Path.GetFileName(fileName)}");
                        System.Diagnostics.Debug.WriteLine($"File not found: {fileName}");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        var currentCountNow = Interlocked.Increment(ref currentCount);
                        var skipCountNow = Interlocked.Increment(ref skipCount);
                        var currentProgress = currentCountNow / (float)totalCount;
                        Workspace.SetProgressThreadSafe(currentProgress, $"跳过 {skipCountNow} 个文件 - 权限不足: {Path.GetFileName(fileName)}");
                        System.Diagnostics.Debug.WriteLine($"Access denied: {fileName}");
                    }
                    catch (IOException ex)
                    {
                        var currentCountNow = Interlocked.Increment(ref currentCount);
                        var skipCountNow = Interlocked.Increment(ref skipCount);
                        var currentProgress = currentCountNow / (float)totalCount;
                        Workspace.SetProgressThreadSafe(currentProgress, $"跳过 {skipCountNow} 个文件 - IO错误: {Path.GetFileName(fileName)}");
                        System.Diagnostics.Debug.WriteLine($"IO error loading {fileName}: {ex.Message}");
                    }
                    catch (NotSupportedException)
                    {
                        var currentCountNow = Interlocked.Increment(ref currentCount);
                        var skipCountNow = Interlocked.Increment(ref skipCount);
                        var currentProgress = currentCountNow / (float)totalCount;
                        Workspace.SetProgressThreadSafe(currentProgress, $"跳过 {skipCountNow} 个文件 - 不支持的文件格式: {Path.GetFileName(fileName)}");
                        System.Diagnostics.Debug.WriteLine($"Unsupported file format: {fileName}");
                    }
                    catch (Exception ex)
                    {
                        var currentCountNow = Interlocked.Increment(ref currentCount);
                        var skipCountNow = Interlocked.Increment(ref skipCount);
                        var currentProgress = currentCountNow / (float)totalCount;
                        Workspace.SetProgressThreadSafe(currentProgress, $"跳过 {skipCountNow} 个文件 - 未知错误: {Path.GetFileName(fileName)}");
                        System.Diagnostics.Debug.WriteLine($"Unexpected error loading {fileName}: {ex.Message}");
                    }
                    finally
                    {
                        // 只有在加载失败时才关闭文件流
                        fileStream?.Dispose();
                    }
                }
            });
            
            Workspace.SetProgressThreadSafe(1f, $"完成！成功加载 {successCount} 个文件，跳过 {skipCount} 个文件");
            Workspace.ModifyMutex.ReleaseMutex();
        });

        if (Workspace.Manager.ClassDatabase == null)
        {
            var anySerializedItems = false;
            foreach (var rootItem in Workspace.RootItems)
            {
                if (rootItem.ObjectType == WorkspaceItemType.AssetsFile)
                {
                    anySerializedItems = true;
                    break;
                }
                else if (rootItem.ObjectType == WorkspaceItemType.BundleFile)
                {
                    foreach (var childItem in rootItem.Children)
                    {
                        if (rootItem.ObjectType == WorkspaceItemType.AssetsFile)
                        {
                            anySerializedItems = true;
                            break;
                        }
                    }
                }

                if (anySerializedItems)
                {
                    break;
                }
            }

            if (anySerializedItems)
            {
                var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
                var version = await dialogService.ShowDialog(new VersionSelectViewModel());
                if (version != null)
                {
                    Workspace.Manager.LoadClassDatabaseFromPackage(version);
                }
            }
        }
    }

    [RelayCommand]
    public async Task FileOpen()
    {
        try
        {
            var storageProvider = StorageService.GetStorageProvider();
            if (storageProvider is null)
            {
                await MessageBoxUtil.ShowDialog("错误", "无法获取文件存储提供程序");
                return;
            }

            var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "打开文件",
                FileTypeFilter = new FilePickerFileType[]
                {
                    new FilePickerFileType("所有文件 (*.*)") { Patterns = new[] { "*" } }
                },
                AllowMultiple = true
            });

            var fileNames = FileDialogUtils.GetOpenFileDialogFiles(result);
            await OpenFiles(fileNames);
        }
        catch (Exception ex)
        {
            await MessageBoxUtil.ShowDialog("错误", $"打开文件时发生错误: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Error in FileOpen: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task FolderOpen()
    {
        try
        {
            var storageProvider = StorageService.GetStorageProvider();
            if (storageProvider is null)
            {
                await MessageBoxUtil.ShowDialog("错误", "无法获取文件存储提供程序");
                return;
            }

            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择游戏目录",
                AllowMultiple = false
            });

            var folders = FileDialogUtils.GetOpenFolderDialogFolders(result);
            if (folders.Length == 0)
            {
                return;
            }

            // 直接用默认参数（全部开启）
            var scanOptions = new ScanOptions
            {
                IncludeSubdirectories = true,
                ScanCommonUnityDirectories = true,
                ValidateFileTypes = true,
                SkipSmallFiles = true
            };
            await ScanDirectory(folders[0], scanOptions);
        }
        catch (Exception ex)
        {
            await MessageBoxUtil.ShowDialog("错误", $"打开文件夹时发生错误: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Error in FolderOpen: {ex.Message}");
        }
    }

    private async Task ScanDirectory(string directoryPath, ScanOptions options)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        // 显示扫描进度
        Workspace.ProgressValue = 0;
        Workspace.ProgressText = "正在扫描游戏目录...";

        // 使用新的文件扫描工具，带进度回调和选项
        var supportedFiles = await Task.Run(() => FileUtils.ScanUnityGameDirectory(directoryPath, 
            progress => Workspace.SetProgressThreadSafe(Workspace.ProgressValue, progress), options));

        if (supportedFiles.Count == 0)
        {
            Workspace.ProgressText = "未找到可处理的Unity文件";
            return;
        }

        Workspace.ProgressText = $"找到 {supportedFiles.Count} 个可处理的文件，开始加载...";
        await OpenFiles(supportedFiles);
    }

    private async Task DoSaveOverwrite(IEnumerable<WorkspaceItem> items)
    {
        Workspace.ModifyMutex.WaitOne();
        try
        {
            var rootItems = new HashSet<WorkspaceItem>();
            foreach (var item in items)
            {
                var rootItem = item;
                while (rootItem.Parent != null)
                {
                    rootItem = rootItem.Parent;
                }
                rootItems.Add(rootItem);
            }

            // 新增：只保存被修改过的文件
            var itemsToSave = rootItems.Where(i => Workspace.UnsavedItems.Contains(i)).ToList();
            if (itemsToSave.Count == 0)
            {
                Workspace.SetProgressThreadSafe(1f, "没有需要保存的文件");
                return;
            }

            var fileInstsToReload = new HashSet<AssetsFileInstance>();
            var someFailed = false;
            foreach (var item in itemsToSave)
            {
                var success = await Workspace.Save(item);
                if (!success)
                {
                    someFailed = true;
                    continue;
                }

                if (item.Object is AssetsFileInstance fileInst)
                {
                    fileInstsToReload.Add(fileInst);
                }
                else if (item.Object is BundleFileInstance)
                {
                    foreach (var child in item.Children)
                    {
                        if (child.Object is AssetsFileInstance childInst)
                        {
                            fileInstsToReload.Add(childInst);
                        }
                    }
                }
            }

            if (fileInstsToReload.Count == 0)
            {
                if (someFailed)
                    Workspace.SetProgressThreadSafe(1f, "所有文件保存失败（检查是否有写入权限？）");
                else
                    Workspace.SetProgressThreadSafe(1f, "没有打开的文件可保存");
            }
            else
            {
                await ReloadAssetDocuments(fileInstsToReload);
                if (someFailed)
                    Workspace.SetProgressThreadSafe(1f, "已保存（部分失败），已重新加载已保存的打开文件");
                else
                    Workspace.SetProgressThreadSafe(1f, "已保存，已重新加载已保存的打开文件");
            }
        }
        finally
        {
            Workspace.ModifyMutex.ReleaseMutex();
        }
    }

    private async Task DoSaveCopy(IEnumerable<WorkspaceItem> items)
    {
        Workspace.ModifyMutex.WaitOne();
        try
        {
            var rootItems = new HashSet<WorkspaceItem>();
            foreach (var item in items)
            {
                var rootItem = item;
                while (rootItem.Parent != null)
                {
                    rootItem = rootItem.Parent;
                }
                rootItems.Add(rootItem);
            }

            foreach (var item in rootItems)
            {
                await Workspace.SaveAs(item);
            }

            Workspace.SetProgressThreadSafe(1f, "已保存");
        }
        finally
        {
            Workspace.ModifyMutex.ReleaseMutex();
        }
    }

    public async Task FileSave()
    {
        var explorer = _factory.GetDockable<WorkspaceExplorerToolViewModel>("WorkspaceExplorer");
        if (explorer == null)
            return;

        var items = explorer.SelectedItems.Cast<WorkspaceItem>();
        await DoSaveOverwrite(items);
    }

    // more like "save copy as"
    public async Task FileSaveAs()
    {
        var explorer = _factory.GetDockable<WorkspaceExplorerToolViewModel>("WorkspaceExplorer");
        if (explorer == null)
            return;

        var items = explorer.SelectedItems.Cast<WorkspaceItem>();
        await DoSaveCopy(items);
    }

    public async Task FileSaveAllAs()
    {
        await DoSaveCopy(Workspace.RootItems);
    }

    public void FileCloseAll()
    {
        Workspace.CloseAll();
        WeakReferenceMessenger.Default.Send(new WorkspaceClosingMessage());

        var files = _factory.GetDockable<IDocumentDock>("Files");
        if (files is not null && files.VisibleDockables != null && files.VisibleDockables.Count > 0)
        {
            // lol you have to pass in a child
            _factory.CloseAllDockables(files.VisibleDockables[0]);
        }
    }

    public void ViewDuplicateTab()
    {
        var files = _factory.GetDockable<IDocumentDock>("Files");
        if (Layout is not null && files is not null)
        {
            if (files.ActiveDockable != null)
            {
                var oldDockable = files.ActiveDockable;
                _factory.AddDockable(files, oldDockable);
            }
        }
    }

    public void ToolsXrefs()
    {
    }

    // todo should we just replace every assetinst? is that too expensive?
    // would it be better than unselecting everything?
    private async Task ReloadAssetDocuments(HashSet<AssetsFileInstance> fileInst)
    {
        var files = _factory.GetDockable<IDocumentDock>("Files");
        if (Layout is not null && files is not null && files.VisibleDockables is not null)
        {
            foreach (var dockable in files.VisibleDockables)
            {
                if (dockable is not AssetDocumentViewModel document)
                    continue;

                var matchesAny = document.FileInsts.Intersect(fileInst).Any();
                if (matchesAny)
                {
                    await document.Load(document.FileInsts);
                }
            }
        }
    }

    private async Task OnSelectedWorkspaceItemsChanged(object recipient, SelectedWorkspaceItemChangedMessage message)
    {
        var workspaceItems = message.Value;

        AssetDocumentViewModel document;
        if (workspaceItems.Count == 1)
        {
            var workspaceItem = workspaceItems[0];

            if (workspaceItem.ObjectType != WorkspaceItemType.AssetsFile)
                return;

            if (workspaceItem.Object is not AssetsFileInstance mainFileInst)
                return;

            document = new AssetDocumentViewModel(Workspace)
            {
                Title = mainFileInst.name,
                Id = mainFileInst.name
            };

            _lastLoadedFiles = [mainFileInst];
            await document.Load(_lastLoadedFiles);
        }
        else
        {
            var assetsFileItems = workspaceItems
                .Where(i => i.ObjectType == WorkspaceItemType.AssetsFile)
                .Select(i => (AssetsFileInstance?)i.Object)
                .Where(i => i != null)
                .ToList();

            if (assetsFileItems.Count == 0)
                return;

            if (assetsFileItems[0] is not AssetsFileInstance mainFileInst)
                return;

            document = new AssetDocumentViewModel(Workspace)
            {
                Title = $"{mainFileInst.name} 和 {assetsFileItems.Count - 1} 个其他文件"
            };

            _lastLoadedFiles = assetsFileItems!;
            await document.Load(_lastLoadedFiles);
        }

        var files = _factory.GetDockable<IDocumentDock>("Files");
        if (Layout is not null && files is not null)
        {
            if (files.ActiveDockable != null)
            {
                var oldDockable = files.ActiveDockable;
                _factory.AddDockable(files, document);
                _factory.SwapDockable(files, oldDockable, document);
                _factory.CloseDockable(oldDockable);
                _factory.SetActiveDockable(document);
                _factory.SetFocusedDockable(files, document);
            }
            else
            {
                _factory.AddDockable(files, document);
                _factory.SetActiveDockable(document);
                _factory.SetFocusedDockable(files, document);
            }
        }
    }

    private void OnRequestEditAsset(object recipient, RequestEditAssetMessage message)
    {
        _ = ShowEditAssetDialog(message.Value);
    }

    private async Task ShowEditAssetDialog(AssetInst asset)
    {
        var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
        var baseField = Workspace.GetBaseField(asset);
        if (baseField == null)
        {
            return;
        }

        var refMan = Workspace.Manager.GetRefTypeManager(asset.FileInstance);
        Workspace.CheckAndSetMonoTempGenerators(asset.FileInstance, asset);
        var newData = await dialogService.ShowDialog(new EditDataViewModel(baseField, refMan));
        if (newData != null)
        {
            asset.UpdateAssetDataAndRow(Workspace, newData);
            WeakReferenceMessenger.Default.Send(new AssetsUpdatedMessage(asset));
        }
    }

    public async Task ShowAssetInfoDialog()
    {
        var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
        var explorer = _factory.GetDockable<WorkspaceExplorerToolViewModel>("WorkspaceExplorer");

        if (explorer is null)
        {
            return;
        }

        HashSet<WorkspaceItem> items = [];
        if (explorer.SelectedItems.Count != 0)
        {
            foreach (var item in WorkspaceItem.GetAssetsFileWorkspaceItems(explorer.SelectedItems.OfType<WorkspaceItem>()))
            {
                items.Add(item);
            }
        }
        else
        {
            foreach (var item in WorkspaceItem.GetAssetsFileWorkspaceItems(Workspace.RootItems))
            {
                items.Add(item);
            }
        }

        await dialogService.ShowDialog(new AssetInfoViewModel(Workspace, items));
    }
}

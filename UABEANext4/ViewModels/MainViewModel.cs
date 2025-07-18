using AssetsTools.NET.Extra;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
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

    public async Task OpenFiles(IEnumerable<string?> enumerable)
    {
        int totalCount = enumerable.Count();
        if (totalCount == 0)
        {
            return;
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount - 1, 4)
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
                    try
                    {
                        var fileStream = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var file = Workspace.LoadAnyFile(fileStream, startLoadOrder + (int)index);
                        var currentCountNow = Interlocked.Increment(ref currentCount);
                        var successCountNow = Interlocked.Increment(ref successCount);
                        var currentProgress = currentCountNow / (float)totalCount;
                        Workspace.SetProgressThreadSafe(currentProgress, $"已加载 {successCountNow}/{totalCount} - {Path.GetFileName(fileName)}");
                    }
                    catch
                    {
                        var currentCountNow = Interlocked.Increment(ref currentCount);
                        var skipCountNow = Interlocked.Increment(ref skipCount);
                        var currentProgress = currentCountNow / (float)totalCount;
                        Workspace.SetProgressThreadSafe(currentProgress, $"跳过 {skipCountNow} 个文件 - {Path.GetFileName(fileName)}");
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

    public async void FileOpen()
    {
        var storageProvider = StorageService.GetStorageProvider();
        if (storageProvider is null)
        {
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

    public async void FolderOpen()
    {
        var storageProvider = StorageService.GetStorageProvider();
        if (storageProvider is null)
        {
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

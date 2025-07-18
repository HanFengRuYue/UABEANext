using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Avalonia.Collections;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using DynamicData;
using DynamicData.Binding;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UABEANext4.AssetWorkspace;
using UABEANext4.Logic;
using UABEANext4.Logic.ImportExport;
using UABEANext4.Plugins;
using UABEANext4.Services;
using UABEANext4.Util;
using UABEANext4.ViewModels.Dialogs;

namespace UABEANext4.ViewModels.Documents;
public partial class AssetDocumentViewModel : Document
{
    const string TOOL_TITLE = "资源文档";

    public Workspace Workspace { get; }

    public List<AssetInst> SelectedItems { get; set; }
    public List<AssetsFileInstance> FileInsts { get; set; }

    public ReadOnlyObservableCollection<AssetInst> Items { get; set; }

    public Dictionary<AssetClassID, string> ClassIdToString { get; }

    [ObservableProperty]
    public DataGridCollectionView _collectionView;
    [ObservableProperty]
    public string _searchText = "";
    [ObservableProperty]
    public ObservableCollection<PluginItemInfo> _pluginsItems;

    // 新增过滤相关属性
    [ObservableProperty]
    public bool _isFilterPanelVisible = false;
    [ObservableProperty]
    public AssetClassID? _selectedAssetType = null;  // 改为可空类型，null表示"全部"
    [ObservableProperty]
    public string _selectedFileName = "";
    [ObservableProperty]
    public bool _showModifiedOnly = false;
    [ObservableProperty]
    public bool _showUnmodifiedOnly = false;
    [ObservableProperty]
    public long _minSize = 0;
    [ObservableProperty]
    public long _maxSize = long.MaxValue;
    [ObservableProperty]
    public string _sizeFilterText = "";
    [ObservableProperty]
    public int _totalItems = 0;
    [ObservableProperty]
    public int _filteredItems = 0;
    [ObservableProperty]
    public string _typeSearchText = "";

    public ObservableCollection<AssetClassID?> AvailableAssetTypes { get; }
    public ObservableCollection<AssetClassID?> FilteredAssetTypes { get; }

    // 命令属性
    [RelayCommand]
    public void ToggleFilterPanel() => IsFilterPanelVisible = !IsFilterPanelVisible;

    [RelayCommand]
    public void ClearAllFilters()
    {
        SearchText = "";
        SelectedAssetType = null;
        SelectedFileName = "";
        ShowModifiedOnly = false;
        ShowUnmodifiedOnly = false;
        MinSize = 0;
        MaxSize = long.MaxValue;
        SizeFilterText = "";
        TypeSearchText = "";
        UpdateFilter();
    }

    [RelayCommand]
    public void ApplySizeFilter()
    {
        if (string.IsNullOrEmpty(SizeFilterText))
        {
            MinSize = 0;
            MaxSize = long.MaxValue;
        }
        else
        {
            var parts = SizeFilterText.Split('-');
            if (parts.Length == 2)
            {
                if (long.TryParse(parts[0].Trim(), out long min))
                    MinSize = min;
                if (long.TryParse(parts[1].Trim(), out long max))
                    MaxSize = max;
            }
            else if (long.TryParse(SizeFilterText.Trim(), out long exact))
            {
                MinSize = exact;
                MaxSize = exact;
            }
        }
        UpdateFilter();
    }

    // 添加大小过滤文本变化的实时更新
    partial void OnSizeFilterTextChanged(string value) => ApplySizeFilter();

    public event Action? ShowPluginsContextMenu;

    private readonly Action<string> _setDataGridFilterDb;

    private IDisposable? _disposableLastList;
    private CancellationToken? _cancellationToken;

    [Obsolete("This constructor is for the designer only and should not be used directly.", true)]
    public AssetDocumentViewModel()
    {
        Workspace = new();
        SelectedItems = new();
        FileInsts = new();
        Items = new(new ObservableCollection<AssetInst>());
        CollectionView = new DataGridCollectionView(new List<object>());
        ClassIdToString = Enum
            .GetValues(typeof(AssetClassID))
            .Cast<AssetClassID>()
            .ToDictionary(enm => enm, enm => enm.ToString());

        AvailableAssetTypes = new ObservableCollection<AssetClassID?>();
        AvailableAssetTypes.Add(null); // 添加"全部"选项
        var sortedTypes = Enum.GetValues<AssetClassID>().OrderBy(x => x.ToString());
        foreach (var type in sortedTypes)
        {
            AvailableAssetTypes.Add(type);
        }
        FilteredAssetTypes = new ObservableCollection<AssetClassID?>(AvailableAssetTypes);
        PluginsItems = new();

        Id = TOOL_TITLE.Replace(" ", "");
        Title = TOOL_TITLE;

        _setDataGridFilterDb = DebounceUtils.Debounce<string>((searchText) =>
        {
            UpdateFilter();
        }, 300);
    }

    public AssetDocumentViewModel(Workspace workspace)
    {
        Workspace = workspace;
        SelectedItems = new();
        FileInsts = new();
        Items = new(new ObservableCollection<AssetInst>());
        CollectionView = new DataGridCollectionView(new List<object>());
        ClassIdToString = Enum
            .GetValues(typeof(AssetClassID))
            .Cast<AssetClassID>()
            .ToDictionary(enm => enm, enm => enm.ToString());

        AvailableAssetTypes = new ObservableCollection<AssetClassID?>();
        AvailableAssetTypes.Add(null); // 添加"全部"选项
        var sortedTypes = Enum.GetValues<AssetClassID>().OrderBy(x => x.ToString());
        foreach (var type in sortedTypes)
        {
            AvailableAssetTypes.Add(type);
        }
        FilteredAssetTypes = new ObservableCollection<AssetClassID?>(AvailableAssetTypes);
        PluginsItems = new();

        Id = TOOL_TITLE.Replace(" ", "");
        Title = TOOL_TITLE;

        _setDataGridFilterDb = DebounceUtils.Debounce<string>((searchText) =>
        {
            UpdateFilter();
        }, 300);

        WeakReferenceMessenger.Default.Register<WorkspaceClosingMessage>(this, (r, h) => _ = OnWorkspaceClosing(r, h));
    }

    partial void OnSearchTextChanged(string value) => _setDataGridFilterDb(value);
    partial void OnSelectedAssetTypeChanged(AssetClassID? value) => UpdateFilter();
    partial void OnSelectedFileNameChanged(string value) => UpdateFilter();
    partial void OnShowModifiedOnlyChanged(bool value) => UpdateFilter();
    partial void OnShowUnmodifiedOnlyChanged(bool value) => UpdateFilter();
    partial void OnMinSizeChanged(long value) => UpdateFilter();
    partial void OnMaxSizeChanged(long value) => UpdateFilter();
    partial void OnTypeSearchTextChanged(string value) => UpdateTypeFilter();

    private void UpdateTypeFilter()
    {
        FilteredAssetTypes.Clear();
        var filteredTypes = AvailableAssetTypes
            .Where(type => type == null || string.IsNullOrEmpty(TypeSearchText) || 
                          type?.ToString().Contains(TypeSearchText, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        
        foreach (var type in filteredTypes)
        {
            FilteredAssetTypes.Add(type);
        }
    }

    private void UpdateFilter()
    {
        System.Diagnostics.Debug.WriteLine($"UpdateFilter被调用 - SelectedAssetType: {SelectedAssetType}");
        
        if (Items != null)
        {
            // 重新创建CollectionView以确保过滤器生效
            var newCollectionView = new DataGridCollectionView(Items);
            newCollectionView.Filter = CreateAdvancedFilter();
            CollectionView = newCollectionView;
            UpdateItemCounts();
            
            System.Diagnostics.Debug.WriteLine($"过滤后项目数: {FilteredItems}/{TotalItems}");
        }
    }

    private void UpdateItemCounts()
    {
        if (Items != null)
        {
            TotalItems = Items.Count;
            FilteredItems = CollectionView?.Count ?? 0;
        }
    }

    private Func<object, bool> CreateAdvancedFilter()
    {
        return o =>
        {
            if (o is not AssetInst asset)
                return false;

            // 文本搜索过滤
            if (!string.IsNullOrEmpty(SearchText))
            {
                bool textMatch = asset.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                                (ClassIdToString.TryGetValue(asset.Type, out string? classIdName) && 
                                 classIdName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) ||
                                asset.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
                if (!textMatch) return false;
            }

            // 资源类型过滤
            if (SelectedAssetType != null && asset.Type != SelectedAssetType)
            {
                System.Diagnostics.Debug.WriteLine($"过滤掉类型: {asset.Type}, 选中类型: {SelectedAssetType}");
                return false;
            }

            // 文件名过滤
            if (!string.IsNullOrEmpty(SelectedFileName) && 
                !asset.FileName.Contains(SelectedFileName, StringComparison.OrdinalIgnoreCase))
                return false;

            // 修改状态过滤
            if (ShowModifiedOnly && asset.Replacer == null)
                return false;
            if (ShowUnmodifiedOnly && asset.Replacer != null)
                return false;

            // 大小过滤
            if (asset.ByteSizeModified < MinSize || asset.ByteSizeModified > MaxSize)
                return false;

            return true;
        };
    }

    private Func<object, bool> SetDataGridFilter(string searchText)
    {
        if (string.IsNullOrEmpty(searchText))
            return a => true;

        return o =>
        {
            if (o is not AssetInst a)
                return false;

            if (a.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                return true;

            if (ClassIdToString.TryGetValue(a.Type, out string? classIdName) && classIdName == searchText)
                return true;

            return false;
        };
    }



    public async Task Load(List<AssetsFileInstance> fileInsts)
    {
        if (Workspace == null)
            return;

        _disposableLastList?.Dispose();

        var sourceList = new SourceList<RangeObservableCollection<AssetFileInfo>>();
        var tasks = new List<Task>();
        _cancellationToken = new CancellationToken();
        await Task.Run(() =>
        {
            foreach (var fileInst in fileInsts)
            {
                var infosObsCol = (RangeObservableCollection<AssetFileInfo>)fileInst.file.Metadata.AssetInfos;
                sourceList.Add(infosObsCol);
            }
        }, _cancellationToken.Value);

        var observableList = sourceList
            .Connect()
            .MergeMany(e => e.ToObservableChangeSet())
            .Transform(a => (AssetInst)a);

        _disposableLastList = observableList.Bind(out var items).Subscribe();
        Items = items;
        FileInsts = fileInsts;

        CollectionView = new DataGridCollectionView(Items);
        CollectionView.Filter = CreateAdvancedFilter();
        UpdateItemCounts();
    }

    public void ViewScene()
    {
        if (SelectedItems.Count >= 1)
        {
            WeakReferenceMessenger.Default.Send(new RequestSceneViewMessage(SelectedItems.First()));
        }
    }

    public void Import()
    {
        if (SelectedItems.Count > 1)
        {
            ImportBatch(SelectedItems.ToList());
        }
        else if (SelectedItems.Count == 1)
        {
            ImportSingle(SelectedItems.First());
        }
    }

    public async void ImportBatch(List<AssetInst> assets)
    {
        var storageProvider = StorageService.GetStorageProvider();
        if (storageProvider is null)
        {
            return;
        }

        var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导入文件夹",
            AllowMultiple = false
        });

        var folders = FileDialogUtils.GetOpenFolderDialogFolders(result);
        if (folders == null || folders.Length != 1)
            return;

        List<string> exts = new List<string>()
        {
            "json", "txt", "dat"
        };

        var dialogService = Ioc.Default.GetRequiredService<IDialogService>();

        var fileNamesToDirty = new HashSet<string>();
        var batchInfos = await dialogService.ShowDialog(new BatchImportViewModel(Workspace, assets, folders[0], exts));
        if (batchInfos == null)
        {
            return;
        }

        foreach (ImportBatchInfo batchInfo in batchInfos)
        {
            var selectedFilePath = batchInfo.ImportFile;
            if (selectedFilePath == null)
                continue;

            var selectedAsset = batchInfo.Asset;
            var selectedInst = selectedAsset.FileInstance;

            using FileStream fs = File.OpenRead(selectedFilePath);

            Workspace.CheckAndSetMonoTempGenerators(selectedInst, selectedAsset);
            var importer = new AssetImport(fs, Workspace.Manager.GetRefTypeManager(selectedInst));

            byte[]? data;
            string? exceptionMessage;

            if (selectedFilePath.EndsWith(".json"))
            {
                var tempField = Workspace.GetTemplateField(selectedAsset);
                data = importer.ImportJsonAsset(tempField, out exceptionMessage);
            }
            else if (selectedFilePath.EndsWith(".txt"))
            {
                data = importer.ImportTextAsset(out exceptionMessage);
            }
            else
            {
                exceptionMessage = string.Empty;
                data = importer.ImportRawAsset();
            }

            if (data == null)
            {
                await MessageBoxUtil.ShowDialog("解析错误", "读取转储文件时出现问题:\n" + exceptionMessage);
                goto dirtyFiles;
            }

            selectedAsset.UpdateAssetDataAndRow(Workspace, data);
            fileNamesToDirty.Add(selectedAsset.FileInstance.name);
        }

    dirtyFiles:
        foreach (var fileName in fileNamesToDirty)
        {
            var fileToDirty = Workspace.ItemLookup[fileName];
            Workspace.Dirty(fileToDirty);
        }
    }

    public async void ImportSingle(AssetInst asset)
    {
        var storageProvider = StorageService.GetStorageProvider();
        if (storageProvider is null)
        {
            return;
        }

        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择导入文件",
            AllowMultiple = true,
            FileTypeFilter = new FilePickerFileType[]
            {
                new FilePickerFileType("UABEA JSON 转储 (*.json)") { Patterns = new[] { "*.json" } },
                new FilePickerFileType("UABE 文本转储 (*.txt)") { Patterns = new[] { "*.txt" } },
                new FilePickerFileType("原始转储 (*.dat)") { Patterns = new[] { "*.dat" } },
                new FilePickerFileType("原始转储 (*.*)") { Patterns = new[] { "*" } },
            },
        });

        var files = FileDialogUtils.GetOpenFileDialogFiles(result);
        if (files == null || files.Length == 0)
            return;

        var file = files[0];
        using var fs = File.OpenRead(file);

        Workspace.CheckAndSetMonoTempGenerators(asset.FileInstance, asset);
        var importer = new AssetImport(fs, Workspace.Manager.GetRefTypeManager(asset.FileInstance));

        byte[]? data = null;
        string? exception;

        if (file.EndsWith(".json") || file.EndsWith(".txt"))
        {
            if (file.EndsWith(".json"))
            {
                var baseField = Workspace.GetTemplateField(asset);
                if (baseField != null)
                {
                    data = importer.ImportJsonAsset(baseField, out exception);
                }
                else
                {
                    // handle template read error
                }
            }
            else if (file.EndsWith(".txt"))
            {
                data = importer.ImportTextAsset(out exception);
            }
        }
        else //if (file.EndsWith(".dat"))
        {
            using var stream = File.OpenRead(file);
            data = importer.ImportRawAsset();
        }

        if (data != null)
        {
            asset.UpdateAssetDataAndRow(Workspace, data);
        }

        var fileToDirty = Workspace.ItemLookup[asset.FileInstance.name];
        Workspace.Dirty(fileToDirty);
    }

    public async void Export()
    {
        var storageProvider = StorageService.GetStorageProvider();
        if (storageProvider is null)
        {
            return;
        }

        var filesToWrite = new List<(AssetInst, string)>();
        if (SelectedItems.Count > 1)
        {
            var dialogService = Ioc.Default.GetRequiredService<IDialogService>();

            var exportType = await dialogService.ShowDialog(new SelectDumpViewModel(true));
            if (exportType == null)
            {
                return;
            }

            // bug fix for double dialog box freezing in windows
            await Task.Yield();

            var exportExt = exportType switch
            {
                SelectedDumpType.JsonDump => ".json",
                SelectedDumpType.TxtDump => ".txt",
                SelectedDumpType.RawDump => ".dat",
                _ => ".dat"
            };

            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择导出文件位置",
                AllowMultiple = false
            });

            var folders = FileDialogUtils.GetOpenFolderDialogFolders(result);
            if (folders.Length == 0)
            {
                return;
            }

            var folder = folders[0];
            foreach (var asset in SelectedItems)
            {
                var exportFileName = Path.Combine(folder, AssetNameUtils.GetAssetFileName(Workspace, asset, exportExt));
                filesToWrite.Add((asset, exportFileName));
            }
        }
        else if (SelectedItems.Count == 1)
        {
            var asset = SelectedItems.First();
            var exportFileName = AssetNameUtils.GetAssetFileName(Workspace, asset, string.Empty);

            var result = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "选择导出文件",
                FileTypeChoices = new FilePickerFileType[]
                {
                    new FilePickerFileType("UABEA JSON 转储 (*.json)") { Patterns = new[] { "*.json" } },
                    new FilePickerFileType("UABE 文本转储 (*.txt)") { Patterns = new[] { "*.txt" } },
                    new FilePickerFileType("原始转储 (*.dat)") { Patterns = new[] { "*.dat" } }
                },
                DefaultExtension = "json",
                SuggestedFileName = exportFileName
            });

            var file = FileDialogUtils.GetSaveFileDialogFile(result);
            if (file == null)
            {
                return;
            }

            filesToWrite.Add((asset, file));
        }

        foreach (var (asset, file) in filesToWrite)
        {
            using var fs = File.OpenWrite(file);
            var exporter = new AssetExport(fs);

            if (file.EndsWith(".json") || file.EndsWith(".txt"))
            {
                var baseField = Workspace.GetBaseField(asset);
                if (baseField == null)
                {
                    fs.Write(Encoding.UTF8.GetBytes("资源反序列化失败。"));
                }
                else
                {
                    if (file.EndsWith(".json"))
                    {
                        exporter.DumpJsonAsset(baseField);
                    }
                    else if (file.EndsWith(".txt"))
                    {
                        exporter.DumpTextAsset(baseField);
                    }
                }
            }
            else if (file.EndsWith(".dat"))
            {
                if (asset.IsReplacerPreviewable)
                {
                    var previewStream = asset.Replacer.GetPreviewStream();
                    var previewReader = new AssetsFileReader(previewStream);
                    lock (previewStream)
                    {
                        exporter.DumpRawAsset(previewReader, 0, (uint)previewStream.Length);
                    }
                }
                else
                {
                    lock (asset.FileInstance.LockReader)
                    {
                        exporter.DumpRawAsset(asset.FileReader, asset.AbsoluteByteStart, asset.ByteSize);
                    }
                }
            }
        }
    }

    public void ShowPlugins()
    {
        if (SelectedItems.Count == 0)
        {
            PluginsItems.Clear();
            PluginsItems.Add(new PluginItemInfo("未选择资源", null, this));
            return;
        }

        var pluginTypes = UavPluginMode.Export | UavPluginMode.Import;
        var pluginsList = Workspace.Plugins.GetOptionsThatSupport(Workspace, SelectedItems, pluginTypes);
        if (pluginsList == null)
        {
            return;
        }

        if (pluginsList.Count == 0)
        {
            PluginsItems.Clear();
            PluginsItems.Add(new PluginItemInfo("无可用插件", null, this));
        }
        else
        {
            PluginsItems.Clear();
            foreach (var plugin in pluginsList)
            {
                PluginsItems.Add(new PluginItemInfo(plugin.Option.Name, plugin.Option, this));
            }
        }

        ShowPluginsContextMenu?.Invoke();
    }

    public void EditDump()
    {
        if (SelectedItems.Count > 0)
        {
            WeakReferenceMessenger.Default.Send(new RequestEditAssetMessage(SelectedItems[^1]));
        }
    }

    public async void AddAsset()
    {
        var dialogService = Ioc.Default.GetRequiredService<IDialogService>();
        var result = await dialogService.ShowDialog(new AddAssetViewModel(Workspace, FileInsts));
        if (result == null)
        {
            return;
        }

        var baseInfo = AssetFileInfo.Create(
            result.File.file, result.PathId, result.TypeId, result.ScriptIndex,
            Workspace.Manager.ClassDatabase, false
        );
        var info = new AssetInst(result.File, baseInfo);
        var baseField = ValueBuilder.DefaultValueFieldFromTemplate(result.TempField);

        result.File.file.Metadata.AddAssetInfo(info);
        info.UpdateAssetDataAndRow(Workspace, baseField);
    }

    public void OnAssetOpened(List<AssetInst> assets)
    {
        if (assets.Count > 0)
        {
            WeakReferenceMessenger.Default.Send(new AssetsSelectedMessage([assets[0]]));
        }

        SelectedItems = assets;
    }

    public void ResendSelectedAssetsSelected()
    {
        if (SelectedItems.Count > 0)
        {
            WeakReferenceMessenger.Default.Send(new AssetsSelectedMessage([SelectedItems[0]]));
        }
    }

    private async Task OnWorkspaceClosing(object recipient, WorkspaceClosingMessage message)
    {
        await Load([]);
    }
}

public class PluginItemInfo
{
    public string Name { get; }

    private IUavPluginOption? _option;
    private AssetDocumentViewModel _docViewModel;

    public PluginItemInfo(string name, IUavPluginOption? option, AssetDocumentViewModel docViewModel)
    {
        Name = name;
        _option = option;
        _docViewModel = docViewModel;
    }

    public async Task Execute(object selectedItems)
    {
        if (_option != null)
        {
            var workspace = _docViewModel.Workspace;
            var res = await _option.Execute(workspace, new UavPluginFunctions(), _option.Options, (List<AssetInst>)selectedItems);
            if (res)
            {
                _docViewModel.ResendSelectedAssetsSelected();
            }
        }
    }

    public override string ToString()
    {
        return Name;
    }
}
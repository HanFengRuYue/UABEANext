using AssetsTools.NET.Extra;
using Avalonia.Platform.Storage;
using System.Text;
using UABEANext4.AssetWorkspace;
using UABEANext4.Plugins;
using UABEANext4.ViewModels.Dialogs;

namespace TextAssetPlugin;
public class ImportTextAssetPlugin : IUavPluginOption
{
    public string Name => "导入文本资源";
    public string Description => "将txt文件导入为文本资源";
    public UavPluginMode Options => UavPluginMode.Import;

    public bool SupportsSelection(Workspace workspace, UavPluginMode mode, IList<AssetInst> selection)
    {
        if (mode != UavPluginMode.Import)
        {
            return false;
        }

        var typeId = (int)AssetClassID.TextAsset;
        return selection.All(a => a.TypeId == typeId);
    }

    public async Task<bool> Execute(Workspace workspace, IUavPluginFunctions funcs, UavPluginMode mode, IList<AssetInst> selection)
    {
        if (selection.Count > 1)
        {
            return await BatchImport(workspace, funcs, selection);
        }
        else
        {
            return await SingleImport(workspace, funcs, selection);
        }
    }

    public async Task<bool> BatchImport(Workspace workspace, IUavPluginFunctions funcs, IList<AssetInst> selection)
    {
        var dir = await funcs.ShowOpenFolderDialog(new FolderPickerOpenOptions()
        {
            Title = "选择导入目录"
        });

        if (dir == null)
        {
            return false;
        }

        var extensions = new List<string>() { "*" };
        var batchInfosViewModel = new BatchImportViewModel(workspace, selection.ToList(), dir, extensions);
        if (batchInfosViewModel.DataGridItems.Count == 0)
        {
            await funcs.ShowMessageDialog("错误", "在目录中未找到匹配的文件。请确保文件名符合UABEA格式。");
            return false;
        }

        var batchInfosResult = await funcs.ShowDialog(batchInfosViewModel);
        if (batchInfosResult == null)
        {
            return false;
        }

        var errorBuilder = new StringBuilder();
        foreach (ImportBatchInfo info in batchInfosResult)
        {
            var asset = info.Asset;
            var errorAssetName = $"{Path.GetFileName(asset.FileInstance.path)}/{asset.PathId}";

            var baseField = workspace.GetBaseField(asset);
            if (baseField == null)
            {
                errorBuilder.AppendLine($"[{errorAssetName}]: failed to read");
                continue;
            }

            var filePath = info.ImportFile;
            if (filePath == null || !File.Exists(filePath))
            {
                errorBuilder.AppendLine($"[{errorAssetName}]: failed to import because {info.ImportFile ?? "[null]"} does not exist.");
                continue;
            }

            byte[] byteData = File.ReadAllBytes(filePath);
            baseField["m_Script"].AsByteArray = byteData;
            asset.UpdateAssetDataAndRow(workspace, baseField);
        }

        if (errorBuilder.Length > 0)
        {
            string[] firstLines = errorBuilder.ToString().Split('\n').Take(20).ToArray();
            string firstLinesStr = string.Join('\n', firstLines);
            await funcs.ShowMessageDialog("错误", firstLinesStr);
        }

        return true;
    }

    public async Task<bool> SingleImport(Workspace workspace, IUavPluginFunctions funcs, IList<AssetInst> selection)
    {
        var filePaths = await funcs.ShowOpenFileDialog(new FilePickerOpenOptions()
        {
            Title = "加载文本资源",
            FileTypeFilter = new List<FilePickerFileType>()
            {
                new("TXT文件 (*.txt)") { Patterns = ["*.txt"] },
                new("BYTES文件 (*.bytes)") { Patterns = ["*.bytes"] },
                new("所有类型 (*.*)") { Patterns = ["*"] },
            },
            AllowMultiple = false
        });

        if (filePaths == null || filePaths.Length == 0)
        {
            return false;
        }

        var filePath = filePaths[0];
        if (!File.Exists(filePath))
        {
            await funcs.ShowMessageDialog("错误", $"导入失败，因为文件 {filePath ?? "[null]"} 不存在。");
            return false;
        }

        var asset = selection[0];
        var baseField = workspace.GetBaseField(asset);
        if (baseField == null)
        {
            await funcs.ShowMessageDialog("错误", "读取失败");
            return false;
        }

        byte[] byteData = File.ReadAllBytes(filePath);
        baseField["m_Script"].AsByteArray = byteData;
        asset.UpdateAssetDataAndRow(workspace, baseField);

        return true;
    }
}

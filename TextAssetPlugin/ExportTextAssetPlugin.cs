using AssetsTools.NET.Extra;
using Avalonia.Platform.Storage;
using System.Text;
using UABEANext4.AssetWorkspace;
using UABEANext4.Plugins;
using UABEANext4.Util;

namespace TextAssetPlugin;
public class ExportTextAssetPlugin : IUavPluginOption
{
    public string Name => "导出文本资源";
    public string Description => "将文本资源导出为txt文件";
    public UavPluginMode Options => UavPluginMode.Export;

    public bool SupportsSelection(Workspace workspace, UavPluginMode mode, IList<AssetInst> selection)
    {
        if (mode != UavPluginMode.Export)
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
            return await BatchExport(workspace, funcs, selection);
        }
        else
        {
            return await SingleExport(workspace, funcs, selection);
        }
    }

    public async Task<bool> BatchExport(Workspace workspace, IUavPluginFunctions funcs, IList<AssetInst> selection)
    {
        var dir = await funcs.ShowOpenFolderDialog(new FolderPickerOpenOptions()
        {
            Title = "选择导出目录"
        });

        if (dir == null)
        {
            return false;
        }

        var errorBuilder = new StringBuilder();
        foreach (var asset in selection)
        {
            var errorAssetName = $"{Path.GetFileName(asset.FileInstance.path)}/{asset.PathId}";
            var textBaseField = workspace.GetBaseField(asset);
            if (textBaseField == null)
            {
                errorBuilder.AppendLine($"[{errorAssetName}]: failed to read");
                continue;
            }

            var name = textBaseField["m_Name"].AsString;
            var byteData = textBaseField["m_Script"].AsByteArray;

            var assetName = PathUtils.ReplaceInvalidPathChars(name);
            var fileName = AssetNameUtils.GetAssetFileName(asset, assetName, ".txt");
            var filePath = Path.Combine(dir, fileName);

            File.WriteAllBytes(filePath, byteData);
        }

        if (errorBuilder.Length > 0)
        {
            string[] firstLines = errorBuilder.ToString().Split('\n').Take(20).ToArray();
            string firstLinesStr = string.Join('\n', firstLines);
            await funcs.ShowMessageDialog("错误", firstLinesStr);
        }

        return true;
    }

    public async Task<bool> SingleExport(Workspace workspace, IUavPluginFunctions funcs, IList<AssetInst> selection)
    {
        var asset = selection[0];
        var textBaseField = workspace.GetBaseField(asset);
        if (textBaseField == null)
        {
            await funcs.ShowMessageDialog("错误", "读取失败");
            return false;
        }

        var name = textBaseField["m_Name"].AsString;
        var byteData = textBaseField["m_Script"].AsByteArray;

        string assetName = PathUtils.ReplaceInvalidPathChars(name);
        var filePath = await funcs.ShowSaveFileDialog(new FilePickerSaveOptions()
        {
            Title = "保存文本资源",
            FileTypeChoices = new List<FilePickerFileType>()
            {
                new("TXT文件 (*.txt)") { Patterns = ["*.txt"] },
                new("BYTES文件 (*.bytes)") { Patterns = ["*.bytes"] },
                new("所有类型 (*.*)") { Patterns = ["*"] },
            },
            SuggestedFileName = AssetNameUtils.GetAssetFileName(asset, assetName, string.Empty),
            DefaultExtension = "txt"
        });

        if (filePath == null)
        {
            return false;
        }

        File.WriteAllBytes(filePath, byteData);
        return true;
    }
}

using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Avalonia.Platform.Storage;
using System.Text;
using TexturePlugin.Helpers;
using TexturePlugin.ViewModels;
using UABEANext4.AssetWorkspace;
using UABEANext4.Plugins;
using UABEANext4.Util;

namespace TexturePlugin;

public class ExportTextureOption : IUavPluginOption
{
    public string Name => "导出纹理2D";
    public string Description => "将Texture2D导出为png/tga/bmp/jpg格式";
    public UavPluginMode Options => UavPluginMode.Export;

    public bool SupportsSelection(Workspace workspace, UavPluginMode mode, IList<AssetInst> selection)
    {
        if (mode != UavPluginMode.Export)
        {
            return false;
        }

        var typeId = (int)AssetClassID.Texture2D;
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
        ExportBatchOptionsViewModel dialog = new ExportBatchOptionsViewModel();
        ExportBatchOptionsResult? optionsRes = await funcs.ShowDialog(dialog);

        // bug fix for double dialog box freezing in windows
        await Task.Yield();

        if (optionsRes == null)
        {
            return false;
        }

        string fileExtension = optionsRes.Value.Extension;
        ImageExportType exportType = optionsRes.Value.ImageType;

        var dir = await funcs.ShowOpenFolderDialog(new FolderPickerOpenOptions()
        {
            Title = "选择导出目录"
        });

        if (dir == null)
        {
            return false;
        }

        StringBuilder errorBuilder = new StringBuilder();
        int emptyTextureCount = 0;
        foreach (AssetInst asset in selection)
        {
            var errorAssetName = $"{Path.GetFileName(asset.FileInstance.path)}/{asset.PathId}";
            var texBaseField = TextureHelper.GetByteArrayTexture(workspace, asset);
            if (texBaseField == null)
            {
                errorBuilder.AppendLine($"[{errorAssetName}]: failed to read");
                continue;
            }

            var texFile = TextureFile.ReadTextureFile(texBaseField);

            TextureHelper.SwizzleOptIn(texFile, asset.FileInstance.file);

            // 0x0 texture, usually called like Font Texture or something
            if (texFile.m_Width == 0 && texFile.m_Height == 0)
            {
                emptyTextureCount++;
                continue;
            }

            string assetName = PathUtils.ReplaceInvalidPathChars(texFile.m_Name);
            string filePath = AssetNameUtils.GetAssetFileName(asset, assetName, fileExtension);

            using FileStream outputStream = File.OpenWrite(Path.Combine(dir, filePath));
            byte[] encTextureData = texFile.FillPictureData(asset.FileInstance);
            bool success = texFile.DecodeTextureImage(encTextureData, outputStream, exportType);
            if (!success)
            {
                errorBuilder.AppendLine($"[{errorAssetName}]: failed to decode (missing resS, invalid texture format, etc.)");
            }
        }

        if (emptyTextureCount == selection.Count)
        {
            await funcs.ShowMessageDialog("错误", "所有纹理都为空。没有纹理被导出。");
            return false;
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
        AssetInst asset = selection[0];

        AssetTypeValueField? texBaseField = TextureHelper.GetByteArrayTexture(workspace, asset);
        TextureFile texFile = TextureFile.ReadTextureFile(texBaseField);

        TextureHelper.SwizzleOptIn(texFile, asset.FileInstance.file);

        // 0x0 texture, usually called like Font Texture or something
        if (texFile.m_Width == 0 && texFile.m_Height == 0)
        {
            await funcs.ShowMessageDialog("错误", "纹理大小为0x0，无法导出。");
            return false;
        }

        string assetName = PathUtils.ReplaceInvalidPathChars(texFile.m_Name);
        var filePath = await funcs.ShowSaveFileDialog(new FilePickerSaveOptions()
        {
            Title = "保存纹理",
            FileTypeChoices = new List<FilePickerFileType>()
            {
                new FilePickerFileType("PNG文件") { Patterns = new List<string>() { "*.png" } },
                new FilePickerFileType("BMP文件") { Patterns = new List<string>() { "*.bmp" } },
                new FilePickerFileType("JPG文件") { Patterns = new List<string>() { "*.jpg", "*.jpeg" } },
                new FilePickerFileType("TGA文件") { Patterns = new List<string>() { "*.tga" } },
            },
            SuggestedFileName = AssetNameUtils.GetAssetFileName(asset, assetName, string.Empty),
            DefaultExtension = "png"
        });

        if (filePath == null)
        {
            return false;
        }

        ImageExportType exportType = Path.GetExtension(filePath) switch
        {
            ".bmp" => ImageExportType.Bmp,
            ".png" => ImageExportType.Png,
            ".jpg" or ".jpeg" => ImageExportType.Jpg,
            ".tga" => ImageExportType.Tga,
            _ => ImageExportType.Png
        };

        using FileStream outputStream = File.OpenWrite(filePath);
        byte[] encTextureData = texFile.FillPictureData(asset.FileInstance);
        bool success = texFile.DecodeTextureImage(encTextureData, outputStream, exportType);
        if (!success)
        {
            string errorAssetName = $"{Path.GetFileName(asset.FileInstance.path)}/{asset.PathId}";
            await funcs.ShowMessageDialog("Error", $"[{errorAssetName}]: failed to decode (missing resS, invalid texture format, etc.)");
        }

        return success;
    }
}

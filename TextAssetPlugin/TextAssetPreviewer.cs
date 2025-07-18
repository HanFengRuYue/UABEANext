using AssetsTools.NET.Extra;
using Avalonia.Media.Imaging;
using System.Text;
using UABEANext4.AssetWorkspace;
using UABEANext4.Logic.Mesh;
using UABEANext4.Plugins;
using System.Linq;

namespace TextAssetPlugin;
public class TextAssetPreviewer : IUavPluginPreviewer
{
    public string Name => "Preview TextAsset";
    public string Description => "Preview TextAssets";

    const int TEXT_ASSET_MAX_LENGTH = 100000;

    public UavPluginPreviewerType SupportsPreview(Workspace workspace, AssetInst selection)
    {
        var previewType = selection.Type == AssetClassID.TextAsset
            ? UavPluginPreviewerType.Text
            : UavPluginPreviewerType.None;

        return previewType;
    }

    public string? ExecuteText(Workspace workspace, IUavPluginFunctions funcs, AssetInst selection, out string? error)
    {
        try
        {
            var textAssetBf = workspace.GetBaseField(selection);
            if (textAssetBf == null)
            {
                error = "No preview available.";
                return null;
            }

            var text = textAssetBf["m_Script"].AsByteArray;
            string trimmedText;

            Encoding[] encodingsToTry = new Encoding[]
            {
                Encoding.UTF8,
                Encoding.Unicode,      // UTF-16 LE
                Encoding.BigEndianUnicode, // UTF-16 BE
                Encoding.GetEncoding("shift_jis")
            };

            string? decoded = null;
            foreach (var enc in encodingsToTry)
            {
                try
                {
                    var candidate = enc.GetString(text);
                    // 如果解码后包含大量替换字符，则认为失败
                    if (candidate.Count(c => c == '\uFFFD') < 10)
                    {
                        decoded = candidate;
                        break;
                    }
                }
                catch { }
            }

            if (decoded == null)
                decoded = Encoding.UTF8.GetString(text); // 兜底

            if (decoded.Length > TEXT_ASSET_MAX_LENGTH)
                trimmedText = decoded.Substring(0, TEXT_ASSET_MAX_LENGTH) + $"... (and {text.Length - TEXT_ASSET_MAX_LENGTH} bytes more)";
            else
                trimmedText = decoded;

            error = null;
            return trimmedText;
        }
        catch (Exception ex)
        {
            error = $"TextAsset failed to decode due to an error. Exception:\n{ex}";
            return null;
        }
    }

    public Bitmap? ExecuteImage(Workspace workspace, IUavPluginFunctions funcs, AssetInst selection, out string? error)
        => throw new InvalidOperationException();

    public MeshObj? ExecuteMesh(Workspace workspace, IUavPluginFunctions funcs, AssetInst selection, out string? error)
        => throw new InvalidOperationException();

    public void Cleanup() { }
}

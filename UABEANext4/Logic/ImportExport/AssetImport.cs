using AssetsTools.NET;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace UABEANext4.Logic.ImportExport;
public class AssetImport : IDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _streamReader;
    private readonly RefTypeManager _refMan;
    private bool _disposed = false;

    public AssetImport(Stream readStream, RefTypeManager refMan)
    {
        _stream = readStream;
        _streamReader = new StreamReader(_stream);
        _refMan = refMan;
    }

    public byte[] ImportRawAsset()
    {
        using var ms = new MemoryStream();
        _stream.CopyTo(ms);
        return ms.ToArray();
    }

    public byte[]? ImportTextAsset(out string? exceptionMessage)
    {
        using var ms = new MemoryStream();
        var writer = new AssetsFileWriter(ms)
        {
            BigEndian = false
        };

        try
        {
            ImportTextAssetLoop(writer);
            exceptionMessage = null;
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.ToString();
            return null;
        }
        return ms.ToArray();
    }

    private void ImportTextAssetLoop(AssetsFileWriter writer)
    {
        Stack<bool> alignStack = new Stack<bool>();
        while (true)
        {
            string? line = _streamReader.ReadLine();
            if (line == null)
                return;

            int thisDepth = 0;
            while (line[thisDepth] == ' ')
                thisDepth++;

            if (line[thisDepth] == '[') // array index, ignore
                continue;

            if (thisDepth < alignStack.Count)
            {
                while (thisDepth < alignStack.Count)
                {
                    if (alignStack.Pop())
                        writer.Align();
                }
            }

            bool align = line.Substring(thisDepth, 1) == "1";
            int typeName = thisDepth + 2;
            int eqSign = line.IndexOf('=');
            string valueStr = line.Substring(eqSign + 1).Trim();

            if (eqSign != -1)
            {
                string check = line.Substring(typeName);

                // 处理字符串类型
                if (AssetTypeHelper.StartsWithSpace(check, "string"))
                {
                    int firstQuote = valueStr.IndexOf('"');
                    int lastQuote = valueStr.LastIndexOf('"');
                    string valueStrFix = valueStr.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
                    valueStrFix = AssetTypeHelper.UnescapeTextDumpString(valueStrFix);
                    writer.WriteCountStringInt32(valueStrFix);
                }
                else
                {
                    // 尝试使用通用类型解析器
                    bool handled = false;
                    foreach (var supportedTypeName in AssetTypeHelper.GetSupportedTypeNames())
                    {
                        if (AssetTypeHelper.StartsWithSpace(check, supportedTypeName))
                        {
                            if (AssetTypeHelper.TryParseValue(supportedTypeName, valueStr, out var parsedValue))
                            {
                                AssetTypeHelper.WriteValue(writer, supportedTypeName, parsedValue!);
                                handled = true;
                                break;
                            }
                        }
                    }
                    
                    if (!handled)
                    {
                        // 如果没有找到匹配的类型，记录警告
                        System.Diagnostics.Debug.WriteLine($"Warning: Unsupported type in text import: {check}");
                    }
                }

                if (align)
                {
                    writer.Align();
                }
            }
            else
            {
                alignStack.Push(align);
            }
        }
    }

    public byte[]? ImportJsonAsset(AssetTypeTemplateField tempField, out string? exceptionMessage)
    {
        using var ms = new MemoryStream();
        var writer = new AssetsFileWriter(ms)
        {
            BigEndian = false
        };

        try
        {
            string jsonText = _streamReader.ReadToEnd();
            JToken token = JToken.Parse(jsonText);

            RecurseJsonImport(writer, tempField, token);
            exceptionMessage = null;
        }
        catch (Exception ex)
        {
            exceptionMessage = ex.ToString();
            return null;
        }
        return ms.ToArray();
    }

    private void RecurseJsonImport(AssetsFileWriter writer, AssetTypeTemplateField tempField, JToken token)
    {
        bool align = tempField.IsAligned;

        if (tempField.Children.Count == 1 && tempField.Children[0].IsArray &&
            token.Type == JTokenType.Array)
        {
            RecurseJsonImport(writer, tempField.Children[0], token);
            return;
        }

        if (!tempField.HasValue && !tempField.IsArray)
        {
            foreach (AssetTypeTemplateField childTempField in tempField.Children)
            {
                JToken? childToken = token[childTempField.Name];

                if (childToken == null)
                {
                    if (tempField != null)
                    {
                        throw new Exception($"Missing field {childTempField.Name} in JSON. Parent field is {tempField.Type} {tempField.Name}.");
                    }
                    else
                    {
                        throw new Exception($"Missing field {childTempField.Name} in JSON.");
                    }
                }

                RecurseJsonImport(writer, childTempField, childToken);
            }

            if (align)
            {
                writer.Align();
            }
        }
        else if (tempField.HasValue && tempField.ValueType == AssetValueType.ManagedReferencesRegistry)
        {
            JsonImportManagedReferencesRegistry(writer, tempField, token);
        }
        else
        {
            switch (tempField.ValueType)
            {
                case AssetValueType.String:
                {
                    align = true;
                    writer.WriteCountStringInt32((string?)token ?? "");
                    break;
                }
                case AssetValueType.ByteArray:
                {
                    JArray byteArrayJArray = ((JArray?)token) ?? new JArray();
                    byte[] byteArrayData = new byte[byteArrayJArray.Count];
                    for (int i = 0; i < byteArrayJArray.Count; i++)
                    {
                        byteArrayData[i] = (byte)byteArrayJArray[i];
                    }
                    writer.Write(byteArrayData.Length);
                    writer.Write(byteArrayData);
                    break;
                }
                default:
                {
                    // 使用通用的类型转换器
                    var convertedValue = AssetTypeHelper.ConvertJTokenToValue(tempField.ValueType, token);
                    WriteValueByType(writer, tempField.ValueType, convertedValue);
                    break;
                }
            }

            // have to do this because of bug in MonoDeserializer
            if (tempField.IsArray && tempField.ValueType != AssetValueType.ByteArray)
            {
                // children[0] is size field, children[1] is the data field
                AssetTypeTemplateField childTempField = tempField.Children[1];

                JArray? tokenArray = (JArray?)token;

                if (tokenArray == null)
                    throw new Exception($"Field {tempField.Name} was not an array in json.");

                writer.Write(tokenArray.Count);
                foreach (JToken childToken in tokenArray.Children())
                {
                    RecurseJsonImport(writer, childTempField, childToken);
                }
            }

            if (align)
            {
                writer.Align();
            }
        }
    }

    private void JsonImportManagedReferencesRegistry(AssetsFileWriter writer, AssetTypeTemplateField tempField, JToken token)
    {
        int version = (int)ExpectAndReadField(token, "version", tempField);
        if (!AssetTypeHelper.IsValidManagedReferencesVersion(version))
        {
            throw new Exception($"ManagedReferencesRegistry version {version} is invalid.");
        }

        JArray refIdsArray = (JArray)ExpectAndReadField(token, "RefIds", tempField);

        writer.Write(version);
        int childCount = refIdsArray.Count;

        // todo: can we not trust the typetree?
        if (version != 1)
        {
            writer.Write(childCount);
        }

        for (int i = 0; i < childCount; i++)
        {
            JToken refdObjectToken = refIdsArray[i];
            long rid = (long)ExpectAndReadField(refdObjectToken, "rid", tempField);
            if (version == 1)
            {
                if (rid != i)
                {
                    throw new Exception($"Field rid must be consecutive. Expected {i}, found {rid}.");
                }
            }
            else
            {
                writer.Write(rid);
            }

            JToken typeToken = ExpectAndReadField(refdObjectToken, "type", tempField);
            AssetTypeReference typeRef = new AssetTypeReference()
            {
                ClassName = (string?)ExpectAndReadField(typeToken, "class", tempField) ?? string.Empty,
                Namespace = (string?)ExpectAndReadField(typeToken, "ns", tempField) ?? string.Empty,
                AsmName = (string?)ExpectAndReadField(typeToken, "asm", tempField) ?? string.Empty
            };

            JToken dataToken = ExpectAndReadField(refdObjectToken, "data", tempField);

            typeRef.WriteAsset(writer);
            if (typeRef.ClassName == string.Empty && typeRef.Namespace == string.Empty && typeRef.AsmName == string.Empty)
            {
                // this is a null entry which has no data after it
                continue;
            }

            AssetTypeTemplateField? objectTempField = _refMan.GetTemplateField(typeRef);
            if (objectTempField == null)
            {
                throw new Exception($"Failed to get managed reference type. Wanted {typeRef.ClassName}.{typeRef.Namespace}"
                    + $" in {typeRef.AsmName} but got a null result.");
            }

            RecurseJsonImport(writer, objectTempField, dataToken);
        }

        if (version == 1)
        {
            AssetTypeReference.TERMINUS.WriteAsset(writer);
        }
        else
        {
            writer.Align();
        }
    }

    private JToken ExpectAndReadField(JToken token, string name, AssetTypeTemplateField? tempField)
    {
        return AssetTypeHelper.ExpectAndReadField(token, name, tempField);
    }

    /// <summary>
    /// 根据AssetValueType写入值
    /// </summary>
    private void WriteValueByType(AssetsFileWriter writer, AssetValueType valueType, object value)
    {
        switch (valueType)
        {
            case AssetValueType.Bool:
                writer.Write((bool)value);
                break;
            case AssetValueType.UInt8:
                writer.Write((byte)value);
                break;
            case AssetValueType.Int8:
                writer.Write((sbyte)value);
                break;
            case AssetValueType.UInt16:
                writer.Write((ushort)value);
                break;
            case AssetValueType.Int16:
                writer.Write((short)value);
                break;
            case AssetValueType.UInt32:
                writer.Write((uint)value);
                break;
            case AssetValueType.Int32:
                writer.Write((int)value);
                break;
            case AssetValueType.UInt64:
                writer.Write((ulong)value);
                break;
            case AssetValueType.Int64:
                writer.Write((long)value);
                break;
            case AssetValueType.Float:
                writer.Write((float)value);
                break;
            case AssetValueType.Double:
                writer.Write((double)value);
                break;
            default:
                throw new ArgumentException($"Unsupported value type: {valueType}");
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // 释放托管资源
                _streamReader?.Dispose();
                // 注意：不要释放 _stream，因为它是传入的，应该由调用者管理
            }
            _disposed = true;
        }
    }

    ~AssetImport()
    {
        Dispose(false);
    }
}

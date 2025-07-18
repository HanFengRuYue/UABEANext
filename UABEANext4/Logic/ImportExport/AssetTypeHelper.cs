using AssetsTools.NET;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace UABEANext4.Logic.ImportExport
{
    /// <summary>
    /// 提供资产类型处理的共享工具方法
    /// </summary>
    public static class AssetTypeHelper
    {
        /// <summary>
        /// 数据类型和其对应的解析器映射
        /// </summary>
        private static readonly Dictionary<string, Func<string, object>> TypeParsers = new()
        {
            { "int", value => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "SInt32", value => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "float", value => float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) },
            { "bool", value => bool.Parse(value) },
            { "SInt64", value => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "long", value => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "UInt8", value => byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "unsigned char", value => byte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "unsigned int", value => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "UInt32", value => uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "UInt16", value => ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "unsigned short", value => ushort.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "SInt8", value => sbyte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "char", value => sbyte.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "SInt16", value => short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "short", value => short.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "UInt64", value => ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "unsigned long long", value => ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) },
            { "double", value => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture) },
            { "FileSize", value => ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture) }
        };

        /// <summary>
        /// 检查字符串是否以指定值开头且后面跟着空格
        /// </summary>
        public static bool StartsWithSpace(string str, string value)
        {
            return str.StartsWith(value + " ");
        }

        /// <summary>
        /// 尝试解析指定类型的值
        /// </summary>
        public static bool TryParseValue(string typeName, string valueStr, out object? result)
        {
            result = null;
            try
            {
                if (TypeParsers.TryGetValue(typeName, out var parser))
                {
                    result = parser(valueStr);
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 写入值到AssetsFileWriter
        /// </summary>
        public static void WriteValue(AssetsFileWriter writer, string typeName, object value)
        {
            switch (typeName)
            {
                case "int":
                case "SInt32":
                    writer.Write((int)value);
                    break;
                case "float":
                    writer.Write((float)value);
                    break;
                case "bool":
                    writer.Write((bool)value);
                    break;
                case "SInt64":
                case "long":
                    writer.Write((long)value);
                    break;
                case "UInt8":
                case "unsigned char":
                    writer.Write((byte)value);
                    break;
                case "unsigned int":
                case "UInt32":
                    writer.Write((uint)value);
                    break;
                case "UInt16":
                case "unsigned short":
                    writer.Write((ushort)value);
                    break;
                case "SInt8":
                case "char":
                    writer.Write((sbyte)value);
                    break;
                case "SInt16":
                case "short":
                    writer.Write((short)value);
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    writer.Write((ulong)value);
                    break;
                case "double":
                    writer.Write((double)value);
                    break;
                default:
                    throw new ArgumentException($"Unsupported type: {typeName}");
            }
        }

        /// <summary>
        /// 转义文本转储字符串
        /// </summary>
        public static string EscapeTextDumpString(string str)
        {
            return str
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        /// <summary>
        /// 反转义文本转储字符串
        /// </summary>
        public static string UnescapeTextDumpString(string str)
        {
            var sb = new StringBuilder(str.Length);
            bool escaping = false;
            
            foreach (char c in str)
            {
                if (!escaping && c == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (escaping)
                {
                    switch (c)
                    {
                        case '\\':
                            sb.Append('\\');
                            break;
                        case 'r':
                            sb.Append('\r');
                            break;
                        case 'n':
                            sb.Append('\n');
                            break;
                        default:
                            sb.Append(c);
                            break;
                    }
                    escaping = false;
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 从JToken转换为对应的C#对象
        /// </summary>
        public static object ConvertJTokenToValue(AssetValueType valueType, JToken token)
        {
            return valueType switch
            {
                AssetValueType.Bool => (bool)token,
                AssetValueType.UInt8 => (byte)token,
                AssetValueType.Int8 => (sbyte)token,
                AssetValueType.UInt16 => (ushort)token,
                AssetValueType.Int16 => (short)token,
                AssetValueType.UInt32 => (uint)token,
                AssetValueType.Int32 => (int)token,
                AssetValueType.UInt64 => (ulong)token,
                AssetValueType.Int64 => (long)token,
                AssetValueType.Float => (float)token,
                AssetValueType.Double => (double)token,
                AssetValueType.String => (string?)token ?? "",
                _ => throw new ArgumentException($"Unsupported value type: {valueType}")
            };
        }

        /// <summary>
        /// 从AssetTypeValueField转换为JToken
        /// </summary>
        public static JToken ConvertValueFieldToJToken(AssetTypeValueField field)
        {
            if (field.Value == null)
            {
                return JValue.CreateNull();
            }

            var valueType = field.Value.ValueType;
            return valueType switch
            {
                AssetValueType.Bool => new JValue(field.AsBool),
                AssetValueType.Int8 or AssetValueType.Int16 or AssetValueType.Int32 => new JValue(field.AsInt),
                AssetValueType.Int64 => new JValue(field.AsLong),
                AssetValueType.UInt8 or AssetValueType.UInt16 or AssetValueType.UInt32 => new JValue(field.AsUInt),
                AssetValueType.UInt64 => new JValue(field.AsULong),
                AssetValueType.String => new JValue(field.AsString),
                AssetValueType.Float => new JValue(field.AsFloat),
                AssetValueType.Double => new JValue(field.AsDouble),
                _ => new JValue("invalid value")
            };
        }

        /// <summary>
        /// 验证ManagedReferencesRegistry版本
        /// </summary>
        public static bool IsValidManagedReferencesVersion(int version)
        {
            return version is >= 1 and <= 2;
        }

        /// <summary>
        /// 创建ManagedReferencesRegistry的JObject表示
        /// </summary>
        public static JObject CreateManagedReferencesJObject(string className, string nameSpace, string asmName)
        {
            return new JObject
            {
                { "class", className },
                { "ns", nameSpace },
                { "asm", asmName }
            };
        }

        /// <summary>
        /// 从JToken中期望并读取字段
        /// </summary>
        public static JToken ExpectAndReadField(JToken token, string fieldName, AssetTypeTemplateField? parentField = null)
        {
            var fieldToken = token[fieldName];
            if (fieldToken == null)
            {
                var parentInfo = parentField != null ? $" Parent field is {parentField.Type} {parentField.Name}." : "";
                throw new Exception($"Missing field {fieldName} in JSON.{parentInfo}");
            }
            return fieldToken;
        }

        /// <summary>
        /// 获取支持的类型名称列表
        /// </summary>
        public static IEnumerable<string> GetSupportedTypeNames()
        {
            return TypeParsers.Keys;
        }

        /// <summary>
        /// 检查指定类型是否被支持
        /// </summary>
        public static bool IsTypeSupported(string typeName)
        {
            return TypeParsers.ContainsKey(typeName);
        }
    }
} 
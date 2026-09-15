using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AdbDeploymentTool;

internal static class ApkMetadata
{
    // APK 中的 AndroidManifest 是 Android 二进制 XML；无需客户额外安装 aapt/Android SDK。
    public static string ReadPackageName(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("AndroidManifest.xml") ?? throw new InvalidDataException("APK 缺少 AndroidManifest.xml。");
        if (entry.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("APK 清单大小不合理。");
        using var input = entry.Open();
        var bytes = new byte[(int)entry.Length]; input.ReadExactly(bytes);
        return ParseManifest(bytes);
    }

    internal static string ParseManifest(byte[] data)
    {
        string? package = null;
        if (data.Length > 1 && data[0] == '<')
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
            package = XDocument.Load(reader).Root?.Attribute("package")?.Value;
        }
        else
        {
            var parser = new BinaryManifest(data);
            package = parser.ReadPackage();
        }
        if (package == null || !Regex.IsMatch(package, @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+\z"))
            throw new InvalidDataException("无法从 APK 读取合法包名。");
        return package;
    }

    private sealed class BinaryManifest(byte[] bytes)
    {
        private string[] _strings = [];
        private void Check(int offset, int length)
        {
            if (offset < 0 || length < 0 || (long)offset + length > bytes.Length) throw new InvalidDataException("APK 二进制清单数据越界。");
        }
        private int U16(int p) { Check(p, 2); return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p)); }
        private uint U32(int p) { Check(p, 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)); }
        private int Int32(int p) { var n = U32(p); if (n > int.MaxValue) throw new InvalidDataException("APK 清单偏移过大。"); return (int)n; }
        private string? StringAt(uint index) => index == uint.MaxValue ? null : index < _strings.Length ? _strings[index] : throw new InvalidDataException("APK 字符串索引无效。");

        public string? ReadPackage()
        {
            if (U16(0) != 0x0003 || Int32(4) != bytes.Length) throw new InvalidDataException("不支持的 AndroidManifest 格式。");
            int offset = U16(2);
            while (offset < bytes.Length)
            {
                int type = U16(offset), headerSize = U16(offset + 2), size = Int32(offset + 4);
                if (headerSize < 8 || size < headerSize) throw new InvalidDataException("APK 清单块格式错误。");
                Check(offset, size);
                if (type == 0x0001) ReadStringPool(offset, headerSize, size);
                if (type == 0x0102)
                {
                    int ext = checked(offset + headerSize);
                    Check(ext, 20);
                    if (StringAt(U32(ext + 4)) == "manifest")
                    {
                        int start = U16(ext + 8), attrSize = U16(ext + 10), count = U16(ext + 12);
                        if (start < 20 || attrSize < 20 || (long)headerSize + start + (long)attrSize * count > size) throw new InvalidDataException("APK 属性区域无效。");
                        for (int i = 0; i < count; i++)
                        {
                            int attr = ext + start + i * attrSize;
                            if (StringAt(U32(attr + 4)) != "package" || U32(attr) != uint.MaxValue) continue;
                            var raw = StringAt(U32(attr + 8));
                            return raw ?? (bytes[attr + 15] == 3 ? StringAt(U32(attr + 16)) : null);
                        }
                    }
                }
                offset += size;
            }
            return null;
        }

        private int Utf8Length(ref int position)
        {
            Check(position, 1); int first = bytes[position++];
            if ((first & 0x80) == 0) return first;
            Check(position, 1); return ((first & 0x7f) << 8) | bytes[position++];
        }
        private void ReadStringPool(int offset, int header, int size)
        {
            if (header < 28) throw new InvalidDataException("APK 字符串池头部无效。");
            int count = Int32(offset + 8), flags = Int32(offset + 16), start = Int32(offset + 20);
            if (count > 100000 || (long)header + count * 4L > size || start < header + count * 4L || start >= size)
                throw new InvalidDataException("APK 字符串池布局无效。");
            _strings = new string[count];
            for (int i = 0; i < count; i++)
            {
                int pos = checked(offset + start + Int32(offset + header + i * 4));
                if (pos >= offset + size) throw new InvalidDataException("APK 字符串超出块边界。");
                int byteLength;
                if ((flags & 0x100) != 0) { Utf8Length(ref pos); byteLength = Utf8Length(ref pos); }
                else
                {
                    int chars = U16(pos); pos += 2;
                    if ((chars & 0x8000) != 0) { chars = checked(((chars & 0x7fff) << 16) | U16(pos)); pos += 2; }
                    byteLength = checked(chars * 2);
                }
                if ((long)pos + byteLength + ((flags & 0x100) != 0 ? 1 : 2) > offset + size) throw new InvalidDataException("APK 字符串长度无效。");
                Check(pos, byteLength);
                _strings[i] = ((flags & 0x100) != 0 ? Encoding.UTF8 : Encoding.Unicode).GetString(bytes, pos, byteLength);
            }
        }
    }
}

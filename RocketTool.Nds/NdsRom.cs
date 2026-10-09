using System.Security.Cryptography;
using System.Text;

namespace RocketTool.Nds;

public sealed record NdsRomIdentity(
    string FileName,
    long Size,
    string Sha256,
    string HeaderTitle,
    string GameCode,
    uint Arm9RomOffset,
    uint Arm9RamAddress,
    uint Arm9Size);

public sealed record NdsRomFile(string Path, uint Offset, uint Size);

public sealed class NdsRom
{
    private readonly byte[] _data;
    private readonly Dictionary<string, NdsRomFile> _files;

    private NdsRom(byte[] data, NdsRomIdentity identity, IReadOnlyList<NdsRomFile> files)
    {
        _data = data;
        Identity = identity;
        Files = files;
        _files = files.ToDictionary(file => file.Path, StringComparer.Ordinal);
    }

    public NdsRomIdentity Identity { get; }
    public IReadOnlyList<NdsRomFile> Files { get; }
    public ReadOnlySpan<byte> Data => _data;

    public static NdsRom Open(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 0x160)
            throw new InvalidDataException("NDS ROM 太小，无法读取卡头。");

        var title = Encoding.ASCII.GetString(data, 0, 12).TrimEnd('\0', ' ');
        var gameCode = Encoding.ASCII.GetString(data, 0x0C, 4);
        var arm9Rom = BitConverter.ToUInt32(data, 0x20);
        var arm9Ram = BitConverter.ToUInt32(data, 0x28);
        var arm9Size = BitConverter.ToUInt32(data, 0x2C);
        var sha256 = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var identity = new NdsRomIdentity(
            Path.GetFileName(path),
            data.Length,
            sha256,
            title,
            gameCode,
            arm9Rom,
            arm9Ram,
            arm9Size);
        return new NdsRom(data, identity, ParseFileSystem(data));
    }

    public NdsRomFile RequireFile(string path)
        => _files.TryGetValue(path, out var file)
            ? file
            : throw new FileNotFoundException($"NDS ROM 中没有文件 {path}。");

    public byte[] ReadFile(string path)
    {
        var file = RequireFile(path);
        var blob = new byte[file.Size];
        _data.AsSpan((int)file.Offset, (int)file.Size).CopyTo(blob);
        return blob;
    }

    public byte[] ReadRange(uint offset, int length)
    {
        if (offset > (uint)_data.Length || offset + (uint)length > (uint)_data.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return _data.AsSpan((int)offset, length).ToArray();
    }

    private static IReadOnlyList<NdsRomFile> ParseFileSystem(byte[] data)
    {
        var fntOffset = BitConverter.ToInt32(data, 0x40);
        var fntSize = BitConverter.ToInt32(data, 0x44);
        var fatOffset = BitConverter.ToInt32(data, 0x48);
        var fatSize = BitConverter.ToInt32(data, 0x4C);
        if (fntOffset < 0 || fntSize < 8 || fatOffset < 0 || fatSize < 8 ||
            fntOffset + fntSize > data.Length || fatOffset + fatSize > data.Length)
            throw new InvalidDataException("NDS FNT/FAT 无效。");

        var fnt = data.AsSpan(fntOffset, fntSize);
        var dirCount = BitConverter.ToUInt16(data, fntOffset + 6);
        var dirs = new (int NameOffset, int FirstFile, int Parent)[dirCount];
        for (var i = 0; i < dirCount; i++)
        {
            var off = fntOffset + i * 8;
            dirs[i] = (
                BitConverter.ToInt32(data, off),
                BitConverter.ToUInt16(data, off + 4),
                BitConverter.ToUInt16(data, off + 6));
        }

        var children = new List<(string Name, bool IsDir, int Id)>[dirCount];
        for (var i = 0; i < dirCount; i++)
            children[i] = [];

        for (var dirIndex = 0; dirIndex < dirCount; dirIndex++)
        {
            var pos = dirs[dirIndex].NameOffset;
            var fileId = dirs[dirIndex].FirstFile;
            while (true)
            {
                var marker = fnt[pos++];
                if (marker == 0) break;
                var isDir = (marker & 0x80) != 0;
                var nameLength = marker & 0x7F;
                var name = Encoding.ASCII.GetString(fnt.Slice(pos, nameLength));
                pos += nameLength;
                if (isDir)
                {
                    var subId = BitConverter.ToUInt16(data, fntOffset + pos);
                    pos += 2;
                    children[dirIndex].Add((name, true, subId));
                }
                else
                {
                    children[dirIndex].Add((name, false, fileId));
                    fileId++;
                }
            }
        }

        var files = new List<NdsRomFile>();
        Walk(0xF000, "");
        return files;

        void Walk(int dirId, string prefix)
        {
            var dirIndex = dirId - 0xF000;
            foreach (var (name, isDir, id) in children[dirIndex])
            {
                var childPath = string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";
                if (isDir)
                {
                    Walk(id, childPath);
                    continue;
                }

                var fatOff = id * 8;
                var start = BitConverter.ToUInt32(data, fatOffset + fatOff);
                var end = BitConverter.ToUInt32(data, fatOffset + fatOff + 4);
                files.Add(new NdsRomFile(childPath, start, end - start));
            }
        }
    }
}

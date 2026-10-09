namespace RocketTool.Nds;

public sealed class NarcFile
{
    private NarcFile(IReadOnlyList<byte[]> files)
    {
        Files = files;
    }

    public IReadOnlyList<byte[]> Files { get; }

    public static NarcFile Parse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 0x1C || !blob[..4].SequenceEqual("NARC"u8))
            throw new InvalidDataException("不是 NARC 文件。");

        var fatb = 0x10;
        if (!blob.Slice(fatb, 4).SequenceEqual("BTAF"u8))
            throw new InvalidDataException("NARC 缺少 BTAF。");
        var fatbSize = BitConverter.ToInt32(blob[(fatb + 4)..]);
        var count = BitConverter.ToInt32(blob[(fatb + 8)..]);
        var fntb = fatb + fatbSize;
        if (fntb < 0 || fntb + 8 > blob.Length)
            throw new InvalidDataException("NARC FNT 越界。");
        var fntMagic = blob.Slice(fntb, 4);
        if (!fntMagic.SequenceEqual("BTNF"u8) && !fntMagic.SequenceEqual("FNTB"u8))
            throw new InvalidDataException("NARC 缺少 BTNF。");
        var fntbSize = BitConverter.ToInt32(blob[(fntb + 4)..]);
        var fimg = fntb + fntbSize;
        if (fimg < 0 || fimg + 8 > blob.Length)
            throw new InvalidDataException("NARC FIMG 越界。");
        var fimgMagic = blob.Slice(fimg, 4);
        if (!fimgMagic.SequenceEqual("GMIF"u8) && !fimgMagic.SequenceEqual("FIMG"u8))
            throw new InvalidDataException("NARC 缺少 GMIF。");
        var baseOffset = fimg + 8;
        var files = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var start = BitConverter.ToInt32(blob[(fatb + 12 + i * 8)..]);
            var end = BitConverter.ToInt32(blob[(fatb + 16 + i * 8)..]);
            if (start < 0 || end < start || baseOffset + end > blob.Length)
                throw new InvalidDataException($"NARC 文件 {i} 越界。");
            files[i] = blob.Slice(baseOffset + start, end - start).ToArray();
        }

        return new NarcFile(files);
    }
}

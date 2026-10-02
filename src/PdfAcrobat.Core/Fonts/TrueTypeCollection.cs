using System.Buffers.Binary;

namespace PdfAcrobat.Core.Fonts;

/// <summary>
/// Extracts a single face from a TrueType Collection (.ttc) as a standalone sfnt (.ttf).
/// PDFsharp cannot read collections, and most Japanese Windows fonts (Yu Gothic, Meiryo,
/// MS Gothic, BIZ UD) ship only as .ttc files.
/// </summary>
public static class TrueTypeCollection
{
    public static bool IsCollection(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data[0] == (byte)'t' && data[1] == (byte)'t' && data[2] == (byte)'c' && data[3] == (byte)'f';

    public static int GetFaceCount(ReadOnlySpan<byte> data) =>
        IsCollection(data) ? (int)BinaryPrimitives.ReadUInt32BigEndian(data[8..]) : 1;

    public static byte[] ExtractFace(ReadOnlySpan<byte> collection, int faceIndex)
    {
        if (!IsCollection(collection))
        {
            throw new ArgumentException("TrueType Collection ではありません。", nameof(collection));
        }

        var faceCount = GetFaceCount(collection);
        if (faceIndex < 0 || faceIndex >= faceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(faceIndex));
        }

        var offsetTable = (int)BinaryPrimitives.ReadUInt32BigEndian(collection[(12 + 4 * faceIndex)..]);
        var sfntVersion = BinaryPrimitives.ReadUInt32BigEndian(collection[offsetTable..]);
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(collection[(offsetTable + 4)..]);

        var records = new (uint Tag, uint Checksum, int Offset, int Length)[tableCount];
        var dataSize = 0;
        for (var i = 0; i < tableCount; i++)
        {
            var record = collection[(offsetTable + 12 + 16 * i)..];
            records[i] = (
                BinaryPrimitives.ReadUInt32BigEndian(record),
                BinaryPrimitives.ReadUInt32BigEndian(record[4..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
            dataSize += Align4(records[i].Length);
        }

        var headerSize = 12 + 16 * tableCount;
        var output = new byte[headerSize + dataSize];
        var span = output.AsSpan();

        var entrySelector = (ushort)Math.Floor(Math.Log2(Math.Max((int)tableCount, 1)));
        var searchRange = (ushort)((1 << entrySelector) * 16);
        BinaryPrimitives.WriteUInt32BigEndian(span, sfntVersion);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], tableCount);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(tableCount * 16 - searchRange));

        var dataOffset = headerSize;
        for (var i = 0; i < tableCount; i++)
        {
            var (tag, checksum, offset, length) = records[i];
            var record = span[(12 + 16 * i)..];
            BinaryPrimitives.WriteUInt32BigEndian(record, tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], checksum);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)dataOffset);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)length);
            collection.Slice(offset, length).CopyTo(span[dataOffset..]);
            dataOffset += Align4(length);
        }

        return output;
    }

    private static int Align4(int value) => (value + 3) & ~3;
}

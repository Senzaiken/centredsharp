using CentrED.Server.Bridge;

namespace CentrED.Tests;

public class HuffmanEncoderTests
{
    private class ClientDecoder
    {
        private static readonly int[] Tree = HuffmanEncoder.DecodeTree.ToArray();
        private int _bitNum = 8;
        private int _value, _mask, _treePos;

        public byte[] Decompress(byte[] src, int expectedSize)
        {
            var dest = new byte[expectedSize];
            int destIndex = 0;
            int srcIndex = 0;

            while (true)
            {
                if (_bitNum >= 8)
                {
                    if (srcIndex >= src.Length)
                        break;
                    _value = src[srcIndex++];
                    _bitNum = 0;
                    _mask = 0x80;
                }

                _treePos = (_value & _mask) != 0 ? Tree[_treePos * 2] : Tree[_treePos * 2 + 1];
                _mask >>= 1;
                _bitNum++;

                if (_treePos <= 0)
                {
                    if (_treePos == -256)
                    {
                        _bitNum = 8;
                        _treePos = 0;
                        continue;
                    }
                    if (destIndex == expectedSize)
                        throw new Exception("Overflow");
                    dest[destIndex++] = (byte)-_treePos;
                    _treePos = 0;
                }
            }
            if (destIndex != expectedSize)
                throw new Exception($"Short output: {destIndex} != {expectedSize}");
            return dest;
        }
    }

    private static byte[] Roundtrip(byte[] input)
    {
        var compressed = new byte[HuffmanEncoder.MaxCompressedSize(input.Length)];
        int len = HuffmanEncoder.Compress(input, compressed);
        return new ClientDecoder().Decompress(compressed[..len], input.Length);
    }

    [Fact]
    public void Roundtrip_SinglePacket()
    {
        var packet = new byte[] { 0x55 };
        Assert.Equal(packet, Roundtrip(packet));
    }

    [Fact]
    public void Roundtrip_AllByteValues()
    {
        var packet = new byte[256];
        for (int i = 0; i < 256; i++)
            packet[i] = (byte)i;
        Assert.Equal(packet, Roundtrip(packet));
    }

    [Fact]
    public void Roundtrip_MultiplePacketsInOneStream()
    {
        var p1 = new byte[] { 0x1B, 0x00, 0x00, 0x00, 0x01, 0xFF };
        var p2 = new byte[] { 0x22, 0x01, 0x41 };

        var c1 = new byte[HuffmanEncoder.MaxCompressedSize(p1.Length)];
        var c2 = new byte[HuffmanEncoder.MaxCompressedSize(p2.Length)];
        int l1 = HuffmanEncoder.Compress(p1, c1);
        int l2 = HuffmanEncoder.Compress(p2, c2);

        var stream = c1[..l1].Concat(c2[..l2]).ToArray();
        var decoded = new ClientDecoder().Decompress(stream, p1.Length + p2.Length);
        Assert.Equal(p1.Concat(p2).ToArray(), decoded);
    }

    [Fact]
    public void Roundtrip_LargeRandomPayload()
    {
        var rng = new Random(1234);
        var packet = new byte[4096];
        rng.NextBytes(packet);
        Assert.Equal(packet, Roundtrip(packet));
    }
}

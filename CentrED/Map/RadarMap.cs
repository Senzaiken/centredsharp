using CentrED.Client;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CentrED.Map;

public class RadarMap
{
    private readonly GraphicsDevice _gd;
    private readonly CentrEDClient _client;
    private Texture2D? _texture;
    public Texture2D? Texture => _texture;
    public bool IsReady => _texture != null;

    public RadarMap(GraphicsDevice gd, CentrEDClient client)
    {
        _gd = gd;
        _client = client;
        client.Connected += OnConnected;
        client.RadarData += RadarData;
        client.RadarUpdate += RadarUpdate;
    }

    public void Refresh()
    {
        if (_client.Running)
            _client.Send(new RequestRadarMapPacket());
    }

    private void OnConnected()
    {
        _texture = new Texture2D(_gd, _client.Width, _client.Height);
        _client.Send(new RequestRadarMapPacket());
    }

    private unsafe void RadarData(ReadOnlySpan<ushort> data)
    {
        if (_texture == null)
            return;
        var width = _client.Width;
        var height = _client.Height;
        uint[] buffer = System.Buffers.ArrayPool<uint>.Shared.Rent(data.Length);
        try
        {
            for (ushort x = 0; x < width; x++)
            {
                for (ushort y = 0; y < height; y++)
                {
                    buffer[y * width + x] = HuesHelper.Color16To32(data[x * height + y]) | 0xFF_00_00_00;
                }
            }

            fixed (uint* ptr = buffer)
            {
                _texture.SetDataPointerEXT(0, null, (IntPtr)ptr, data.Length * sizeof(uint));
            }
        }
        finally
        {
            System.Buffers.ArrayPool<uint>.Shared.Return(buffer);
        }
    }

    private void RadarUpdate(ushort x, ushort y, ushort color)
    {
        _texture?.SetData(0, new Rectangle(x, y, 1, 1), new[] { HuesHelper.Color16To32(color) | 0xFF_00_00_00 }, 0, 1);
    }
}

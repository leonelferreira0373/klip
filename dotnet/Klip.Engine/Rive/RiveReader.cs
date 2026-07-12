using System;
using System.Text;

namespace Klip.Engine.Rive;

/// <summary>
/// Low-level reader for the Rive .riv binary format. LEB128 varuints, little-endian float32,
/// 0xAABBGGRR colors, length-prefixed UTF-8 strings — matching rive/core/binary_reader.cpp.
/// </summary>
public sealed class RiveReader
{
    private readonly byte[] _d;
    private int _pos;

    public RiveReader(byte[] data) { _d = data; _pos = 0; }

    public int Position => _pos;
    public bool End => _pos >= _d.Length;
    public int Remaining => _d.Length - _pos;

    public byte ReadByte()
    {
        if (_pos >= _d.Length) throw new EndOfStreamRive();
        return _d[_pos++];
    }

    /// <summary>Unsigned LEB128 varint (Rive's readVarUint).</summary>
    public ulong ReadVarUint()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new InvalidOperationException("varuint too long");
        }
        return result;
    }

    public uint ReadVarUint32() => (uint)ReadVarUint();

    /// <summary>Little-endian IEEE-754 float32 (Rive's readFloat32).</summary>
    public float ReadFloat32()
    {
        if (_pos + 4 > _d.Length) throw new EndOfStreamRive();
        float v = BitConverter.ToSingle(_d, _pos);   // .NET is little-endian on all supported platforms
        _pos += 4;
        return v;
    }

    /// <summary>Rive color: 4 bytes little-endian → 0xAARRGGBB in memory as stored ABGR? See runtime.
    /// rive stores color as a uint (0xAARRGGBB). readVarUint is NOT used — colors are 4 raw bytes.</summary>
    public uint ReadColorU32()
    {
        if (_pos + 4 > _d.Length) throw new EndOfStreamRive();
        uint v = (uint)(_d[_pos] | (_d[_pos + 1] << 8) | (_d[_pos + 2] << 16) | (_d[_pos + 3] << 24));
        _pos += 4;
        return v;
    }

    /// <summary>Length-prefixed (varuint) UTF-8 string.</summary>
    public string ReadString()
    {
        int len = (int)ReadVarUint();
        if (len == 0) return string.Empty;
        if (_pos + len > _d.Length) throw new EndOfStreamRive();
        var s = Encoding.UTF8.GetString(_d, _pos, len);
        _pos += len;
        return s;
    }

    public byte[] ReadBytes(int n)
    {
        if (_pos + n > _d.Length) throw new EndOfStreamRive();
        var b = new byte[n];
        Array.Copy(_d, _pos, b, 0, n);
        _pos += n;
        return b;
    }

    public void Skip(int n) { _pos = Math.Min(_d.Length, _pos + n); }
}

public sealed class EndOfStreamRive : Exception
{
    public EndOfStreamRive() : base("unexpected end of .riv stream") { }
}

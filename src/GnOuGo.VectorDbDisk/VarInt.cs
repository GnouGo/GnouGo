namespace GnOuGo.VectorDbDisk;

internal static class VarInt
{
    public static void WriteUInt32(Stream s, uint value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    public static uint ReadUInt32(Stream s)
    {
        uint result = 0;
        int shift = 0;

        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();

            result |= ((uint)(b & 0x7F)) << shift;
            if ((b & 0x80) == 0) return result;

            shift += 7;
            if (shift > 35) throw new InvalidDataException("VarInt too long.");
        }
    }
}

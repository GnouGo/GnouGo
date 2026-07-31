namespace GnOuGo.Assets.Animation;

internal struct StableRandom
{
    private uint _state;

    public StableRandom(int seed)
    {
        _state = unchecked((uint)seed);
        if (_state == 0)
            _state = 0x9E3779B9u;
    }

    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        return (int)(NextUInt() % (uint)maxExclusive);
    }

    public bool NextBool() => (NextUInt() & 1u) == 1u;
}

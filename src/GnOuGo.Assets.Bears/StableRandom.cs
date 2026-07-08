namespace GnOuGo.Assets.Bears;

internal struct StableRandom
{
    private uint _state;

    public StableRandom(int seed)
    {
        _state = unchecked((uint)seed);
        if (_state == 0)
            _state = 0x6D2B79F5u;
    }

    public int NextInclusive(int minValue, int maxValue)
    {
        if (minValue > maxValue)
            throw new ArgumentOutOfRangeException(nameof(minValue));

        var range = (uint)(maxValue - minValue + 1);
        return minValue + (int)(NextUInt32() % range);
    }

    private uint NextUInt32()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }
}

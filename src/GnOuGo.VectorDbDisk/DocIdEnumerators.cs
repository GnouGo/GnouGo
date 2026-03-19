namespace GnOuGo.VectorDbDisk;

internal interface IDocIdEnumerator : IDisposable
{
    int Current { get; }
    bool MoveNext();
}

internal sealed class EmptyEnumerator : IDocIdEnumerator
{
    public int Current => -1;
    public bool MoveNext() => false;
    public void Dispose() { }
    public static readonly EmptyEnumerator Instance = new();
}

internal sealed class RangeEnumerator : IDocIdEnumerator
{
    private readonly int _endExclusive;
    private int _cur;

    public int Current => _cur;

    public RangeEnumerator(int startInclusive, int endExclusive)
    {
        _cur = startInclusive - 1;
        _endExclusive = endExclusive;
    }

    public bool MoveNext()
    {
        int next = _cur + 1;
        if (next >= _endExclusive) return false;
        _cur = next;
        return true;
    }

    public void Dispose() { }
}

internal sealed class PostingEnumeratorAdapter : IDocIdEnumerator
{
    private readonly PostingsEnumerator _inner;
    public int Current { get; private set; }

    public PostingEnumeratorAdapter(string postingPath)
    {
        _inner = MetadataPostings.OpenEnumerator(postingPath);
    }

    public bool MoveNext()
    {
        if (!_inner.MoveNext()) return false;
        Current = _inner.Current;
        return true;
    }

    public void Dispose() => _inner.Dispose();
}

internal sealed class UnionEnumerator : IDocIdEnumerator
{
    private readonly IDocIdEnumerator _a;
    private readonly IDocIdEnumerator _b;
    private bool _ha;
    private bool _hb;

    public int Current { get; private set; }

    public UnionEnumerator(IDocIdEnumerator a, IDocIdEnumerator b)
    {
        _a = a;
        _b = b;
        _ha = _a.MoveNext();
        _hb = _b.MoveNext();
    }

    public bool MoveNext()
    {
        if (!_ha && !_hb) return false;

        int next;
        if (_ha && _hb)
        {
            if (_a.Current == _b.Current)
            {
                next = _a.Current;
                _ha = _a.MoveNext();
                _hb = _b.MoveNext();
            }
            else if (_a.Current < _b.Current)
            {
                next = _a.Current;
                _ha = _a.MoveNext();
            }
            else
            {
                next = _b.Current;
                _hb = _b.MoveNext();
            }
        }
        else if (_ha)
        {
            next = _a.Current;
            _ha = _a.MoveNext();
        }
        else
        {
            next = _b.Current;
            _hb = _b.MoveNext();
        }

        Current = next;
        return true;
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }
}

internal sealed class IntersectEnumerator : IDocIdEnumerator
{
    private readonly IDocIdEnumerator _a;
    private readonly IDocIdEnumerator _b;
    private bool _ha;
    private bool _hb;

    public int Current { get; private set; }

    public IntersectEnumerator(IDocIdEnumerator a, IDocIdEnumerator b)
    {
        _a = a;
        _b = b;
        _ha = _a.MoveNext();
        _hb = _b.MoveNext();
    }

    public bool MoveNext()
    {
        while (_ha && _hb)
        {
            int ca = _a.Current;
            int cb = _b.Current;

            if (ca == cb)
            {
                Current = ca;
                _ha = _a.MoveNext();
                _hb = _b.MoveNext();
                return true;
            }
            else if (ca < cb)
            {
                _ha = _a.MoveNext();
            }
            else
            {
                _hb = _b.MoveNext();
            }
        }
        return false;
    }

    public void Dispose()
    {
        _a.Dispose();
        _b.Dispose();
    }
}

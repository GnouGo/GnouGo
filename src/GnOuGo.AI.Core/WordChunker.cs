using System.Text;

namespace GnOuGo.AI.Core;

/// <summary>
/// Converts token-sized deltas into larger, user-friendly chunks (mostly word-sized)
/// for a typing-like UX when streaming AI responses.
/// </summary>
public sealed class WordChunker
{
    public sealed class State
    {
        private readonly StringBuilder _buffer = new(capacity: 256);

        /// <summary>
        /// Feed a new delta and return the chunks that can be safely flushed.
        /// </summary>
        public IEnumerable<string> Feed(string? delta)
        {
            if (string.IsNullOrEmpty(delta))
                yield break;

            _buffer.Append(delta);

            var lastWhitespace = LastWhitespaceIndex(_buffer);
            if (lastWhitespace >= 0)
            {
                var flush = _buffer.ToString(0, lastWhitespace + 1);
                _buffer.Remove(0, lastWhitespace + 1);
                yield return flush;
                yield break;
            }

            if (_buffer.Length >= 6)
            {
                var last = _buffer[^1];
                if (last is '.' or '!' or '?' or ':' or ';')
                {
                    yield return _buffer.ToString();
                    _buffer.Clear();
                }
            }
        }

        /// <summary>Flushes any remaining content in the buffer.</summary>
        public IEnumerable<string> Flush()
        {
            if (_buffer.Length > 0)
            {
                yield return _buffer.ToString();
                _buffer.Clear();
            }
        }

        private static int LastWhitespaceIndex(StringBuilder sb)
        {
            for (var i = sb.Length - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(sb[i]))
                    return i;
            }
            return -1;
        }
    }

    /// <summary>Creates a new chunking state.</summary>
    public State Create() => new();
}


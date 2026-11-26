namespace Coder.Core.Text;

/// <summary>
/// Piece-table text storage.
/// <para>
/// The file contents are kept once, untouched, in <c>_original</c>. Every inserted string is appended
/// to <c>_added</c>. The document is the concatenation of <see cref="Piece"/> slices over those two
/// buffers, so an edit never copies the document and memory stays close to the file size.
/// </para>
/// <para>
/// Line breaks are always <c>'\n'</c> inside the table (see <see cref="LineEndings"/>). Each buffer keeps
/// a sorted list of its newline positions, which lets every piece count its lines with a binary search.
/// A prefix-sum index over the pieces is rebuilt lazily after edits, making offset and line lookups
/// O(log pieces).
/// </para>
/// </summary>
public sealed class PieceTable
{
    private readonly TextBuffer _original;
    private readonly TextBuffer _added = new();
    private readonly List<Piece> _pieces = [];

    // Lazily rebuilt prefix sums: where each piece starts (chars) and how many newlines precede it.
    private int[] _pieceStarts = [];
    private int[] _newlinesBefore = [];
    private bool _indexIsStale = true;

    public PieceTable(string text)
    {
        _original = TextBuffer.FromString(text);
        if (text.Length > 0)
        {
            _pieces.Add(new Piece(BufferKind.Original, 0, text.Length, _original.CountNewlines(0, text.Length)));
        }

        Length = text.Length;
        NewlineCount = _original.NewlineCount;
    }

    public int Length { get; private set; }

    /// <summary>Total number of <c>'\n'</c> characters in the document.</summary>
    public int NewlineCount { get; private set; }

    public int LineCount => NewlineCount + 1;

    /// <summary>Number of internal slices. Exposed for diagnostics and tests.</summary>
    public int PieceCount => _pieces.Count;

    public char this[int offset]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, Length);
            var index = PieceIndexAt(offset);
            var piece = _pieces[index];
            return BufferOf(piece).Text[piece.Start + (offset - _pieceStarts[index])];
        }
    }

    public string GetText() => GetText(0, Length);

    public string GetText(int offset, int length)
    {
        ValidateRange(offset, length);
        if (length == 0)
        {
            return string.Empty;
        }

        return string.Create(length, (Table: this, Offset: offset), static (destination, state) =>
            state.Table.CopyTo(state.Offset, destination));
    }

    /// <summary>Copies <c>destination.Length</c> characters starting at <paramref name="offset"/>.</summary>
    public void CopyTo(int offset, Span<char> destination)
    {
        ValidateRange(offset, destination.Length);
        if (destination.IsEmpty)
        {
            return;
        }

        var index = PieceIndexAt(offset);
        var skip = offset - _pieceStarts[index];
        var written = 0;
        while (written < destination.Length)
        {
            var piece = _pieces[index];
            var count = Math.Min(piece.Length - skip, destination.Length - written);
            BufferOf(piece).Text.AsSpan(piece.Start + skip, count).CopyTo(destination[written..]);
            written += count;
            skip = 0;
            index++;
        }
    }

    /// <summary>Offset of the first character of a zero-based line.</summary>
    public int GetLineStart(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(line, NewlineCount);
        if (line == 0)
        {
            return 0;
        }

        // Line N starts right after the N-th newline (1-based).
        EnsureIndex();
        var pieceIndex = PieceIndexContainingNewline(line);
        var piece = _pieces[pieceIndex];
        var newlineInPiece = line - _newlinesBefore[pieceIndex];
        var bufferOffset = BufferOf(piece).NthNewlineFrom(piece.Start, newlineInPiece);
        return _pieceStarts[pieceIndex] + (bufferOffset - piece.Start) + 1;
    }

    /// <summary>Length of a line, excluding its line break.</summary>
    public int GetLineLength(int line)
    {
        var start = GetLineStart(line);
        var end = line < NewlineCount ? GetLineStart(line + 1) - 1 : Length;
        return end - start;
    }

    public string GetLineText(int line) => GetText(GetLineStart(line), GetLineLength(line));

    public int GetLineOfOffset(int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);
        if (offset == 0 || _pieces.Count == 0)
        {
            return 0;
        }

        // Count newlines strictly before the offset.
        var index = PieceIndexAt(offset - 1);
        var piece = _pieces[index];
        var charsIntoPiece = offset - _pieceStarts[index];
        return _newlinesBefore[index] + BufferOf(piece).CountNewlines(piece.Start, charsIntoPiece);
    }

    public void Insert(int offset, string text)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset, Length);
        if (text.Length == 0)
        {
            return;
        }

        var addedStart = _added.Append(text);
        var newlines = _added.CountNewlines(addedStart, text.Length);
        var newPiece = new Piece(BufferKind.Added, addedStart, text.Length, newlines);

        if (!TryExtendPreviousInsert(offset, newPiece))
        {
            InsertPiece(offset, newPiece);
        }

        Length += text.Length;
        NewlineCount += newlines;
        _indexIsStale = true;
    }

    public void Delete(int offset, int length)
    {
        ValidateRange(offset, length);
        if (length == 0)
        {
            return;
        }

        var first = PieceIndexAt(offset);
        var last = PieceIndexAt(offset + length - 1);
        var firstPiece = _pieces[first];
        var lastPiece = _pieces[last];
        var keepBefore = offset - _pieceStarts[first];
        var keepAfterStart = offset + length - _pieceStarts[last];

        var replacements = new List<Piece>(2);
        if (keepBefore > 0)
        {
            replacements.Add(Slice(firstPiece, 0, keepBefore));
        }

        if (keepAfterStart < lastPiece.Length)
        {
            replacements.Add(Slice(lastPiece, keepAfterStart, lastPiece.Length - keepAfterStart));
        }

        var removedNewlines = 0;
        for (var i = first; i <= last; i++)
        {
            removedNewlines += _pieces[i].NewlineCount;
        }

        foreach (var piece in replacements)
        {
            removedNewlines -= piece.NewlineCount;
        }

        _pieces.RemoveRange(first, last - first + 1);
        _pieces.InsertRange(first, replacements);

        Length -= length;
        NewlineCount -= removedNewlines;
        _indexIsStale = true;
    }

    /// <summary>
    /// Typing produces many tiny inserts right after each other. When the new text continues both the
    /// previous piece in the document and the end of the add-buffer, growing that piece keeps the
    /// piece count (and therefore memory and lookup cost) flat.
    /// </summary>
    private bool TryExtendPreviousInsert(int offset, Piece newPiece)
    {
        if (offset == 0 || _pieces.Count == 0)
        {
            return false;
        }

        var index = PieceIndexAt(offset - 1);
        var previous = _pieces[index];
        var endsAtOffset = _pieceStarts[index] + previous.Length == offset;
        var continuesAddBuffer = previous.Buffer == BufferKind.Added && previous.Start + previous.Length == newPiece.Start;
        if (!endsAtOffset || !continuesAddBuffer)
        {
            return false;
        }

        _pieces[index] = previous with
        {
            Length = previous.Length + newPiece.Length,
            NewlineCount = previous.NewlineCount + newPiece.NewlineCount,
        };
        return true;
    }

    private void InsertPiece(int offset, Piece newPiece)
    {
        if (offset == Length)
        {
            _pieces.Add(newPiece);
            return;
        }

        var index = PieceIndexAt(offset);
        var piece = _pieces[index];
        var split = offset - _pieceStarts[index];
        if (split == 0)
        {
            _pieces.Insert(index, newPiece);
            return;
        }

        _pieces[index] = Slice(piece, 0, split);
        _pieces.InsertRange(index + 1, [newPiece, Slice(piece, split, piece.Length - split)]);
    }

    private Piece Slice(Piece piece, int from, int length)
    {
        var start = piece.Start + from;
        return new Piece(piece.Buffer, start, length, BufferOf(piece).CountNewlines(start, length));
    }

    private TextBuffer BufferOf(Piece piece) => piece.Buffer == BufferKind.Original ? _original : _added;

    /// <summary>Index of the piece that contains the character at <paramref name="offset"/>.</summary>
    private int PieceIndexAt(int offset)
    {
        EnsureIndex();
        var index = Array.BinarySearch(_pieceStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>Index of the piece holding the <paramref name="newlineNumber"/>-th newline (1-based).</summary>
    private int PieceIndexContainingNewline(int newlineNumber)
    {
        // First piece whose cumulative newline count reaches newlineNumber.
        int low = 0, high = _pieces.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_newlinesBefore[mid] + _pieces[mid].NewlineCount < newlineNumber)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private void EnsureIndex()
    {
        if (!_indexIsStale)
        {
            return;
        }

        if (_pieceStarts.Length != _pieces.Count)
        {
            _pieceStarts = new int[_pieces.Count];
            _newlinesBefore = new int[_pieces.Count];
        }

        int offset = 0, newlines = 0;
        for (var i = 0; i < _pieces.Count; i++)
        {
            _pieceStarts[i] = offset;
            _newlinesBefore[i] = newlines;
            offset += _pieces[i].Length;
            newlines += _pieces[i].NewlineCount;
        }

        _indexIsStale = false;
    }

    private void ValidateRange(int offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, Length);
    }

    private enum BufferKind : byte
    {
        Original,
        Added,
    }

    private readonly record struct Piece(BufferKind Buffer, int Start, int Length, int NewlineCount);

    /// <summary>An append-only character buffer that remembers where its newlines are.</summary>
    private sealed class TextBuffer
    {
        private readonly List<int> _newlines;
        private char[] _chars;
        private int _length;

        public TextBuffer()
        {
            _chars = new char[256];
            _newlines = [];
        }

        private TextBuffer(char[] chars, List<int> newlines)
        {
            _chars = chars;
            _length = chars.Length;
            _newlines = newlines;
        }

        /// <summary>The live characters. Only indexes below the appended length are meaningful.</summary>
        public char[] Text => _chars;

        public int NewlineCount => _newlines.Count;

        public static TextBuffer FromString(string text)
        {
            var newlines = new List<int>();
            for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
            {
                newlines.Add(i);
            }

            return new TextBuffer(text.ToCharArray(), newlines);
        }

        /// <summary>Appends text and returns the buffer offset where it begins.</summary>
        public int Append(string text)
        {
            var start = _length;
            if (_length + text.Length > _chars.Length)
            {
                Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _length + text.Length));
            }

            text.CopyTo(0, _chars, _length, text.Length);
            for (var i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
            {
                _newlines.Add(start + i);
            }

            _length += text.Length;
            return start;
        }

        public int CountNewlines(int start, int length) =>
            LowerBound(start + length) - LowerBound(start);

        /// <summary>Buffer offset of the n-th newline (1-based) at or after <paramref name="start"/>.</summary>
        public int NthNewlineFrom(int start, int n) => _newlines[LowerBound(start) + n - 1];

        /// <summary>Index of the first newline position that is &gt;= <paramref name="value"/>.</summary>
        private int LowerBound(int value)
        {
            var index = _newlines.BinarySearch(value);
            return index >= 0 ? index : ~index;
        }
    }
}

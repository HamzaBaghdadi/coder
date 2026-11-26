using System.Text;

namespace Coder.Core.Terminal;

/// <summary>Receives what <see cref="VtParser"/> recognizes in the output of a program.</summary>
public interface IVtHandler
{
    /// <summary>A printable character (a Unicode scalar value).</summary>
    void Print(int rune);

    /// <summary>A C0 control character such as LF, CR, BS, HT, BEL.</summary>
    void Execute(char control);

    /// <summary>
    /// A control sequence: ESC [ <paramref name="privateMarker"/>? params <paramref name="intermediates"/> <paramref name="final"/>.
    /// Missing parameters are -1.
    /// </summary>
    void CsiDispatch(IReadOnlyList<int> parameters, char privateMarker, string intermediates, char final);

    /// <summary>An escape sequence: ESC <paramref name="intermediates"/> <paramref name="final"/>.</summary>
    void EscDispatch(string intermediates, char final);

    /// <summary>An operating system command: ESC ] text (BEL | ESC \), e.g. "0;window title".</summary>
    void OscDispatch(string command);
}

/// <summary>
/// Splits terminal output into characters, control characters and escape sequences. A compact version
/// of the classic DEC VT500 parser state machine; it keeps state between calls, so sequences may be
/// split across chunks of output.
/// </summary>
public sealed class VtParser(IVtHandler handler)
{
    private const int MaxParameters = 32;
    private const int MaxStringLength = 4096;

    private readonly List<int> _parameters = [];
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _string = new();
    private State _state = State.Ground;
    private int _currentParameter = -1;
    private char _privateMarker;
    private char _pendingHighSurrogate;
    private bool _stringEscape; // ESC seen inside a string: a following backslash ends it

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParameter,
        CsiIgnore,
        OscString,
        IgnoredString, // DCS, SOS, PM, APC: consumed until the string terminator
    }

    public void Feed(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            Step(c);
        }
    }

    private void Step(char c)
    {
        // Strings end with BEL or ESC \ and may contain other control characters.
        if (_state is State.OscString or State.IgnoredString)
        {
            StepString(c);
            return;
        }

        switch (c)
        {
            case '\x1b':
                BeginEscape();
                return;
            case '\x18' or '\x1a': // CAN, SUB cancel a sequence
                _state = State.Ground;
                return;
            case < ' ' or '\x7f':
                if (c != '\x7f')
                {
                    handler.Execute(c);
                }

                return;
        }

        switch (_state)
        {
            case State.Ground:
                Print(c);
                break;
            case State.Escape:
            case State.EscapeIntermediate:
                StepEscape(c);
                break;
            case State.CsiEntry:
            case State.CsiParameter:
            case State.CsiIgnore:
                StepCsi(c);
                break;
        }
    }

    private void Print(char c)
    {
        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return;
        }

        if (char.IsLowSurrogate(c) && _pendingHighSurrogate != 0)
        {
            handler.Print(char.ConvertToUtf32(_pendingHighSurrogate, c));
            _pendingHighSurrogate = '\0';
            return;
        }

        _pendingHighSurrogate = '\0';
        handler.Print(c);
    }

    private void BeginEscape()
    {
        _state = State.Escape;
        _intermediates.Clear();
    }

    private void StepEscape(char c)
    {
        if (c is >= ' ' and <= '/')
        {
            _intermediates.Append(c);
            _state = State.EscapeIntermediate;
            return;
        }

        if (_state == State.Escape)
        {
            switch (c)
            {
                case '[':
                    _state = State.CsiEntry;
                    _parameters.Clear();
                    _currentParameter = -1;
                    _privateMarker = '\0';
                    return;
                case ']':
                    _state = State.OscString;
                    _string.Clear();
                    return;
                case 'P' or 'X' or '^' or '_':
                    _state = State.IgnoredString;
                    return;
            }
        }

        handler.EscDispatch(_intermediates.ToString(), c);
        _state = State.Ground;
    }

    private void StepCsi(char c)
    {
        if (c is >= '0' and <= '9' && _state != State.CsiIgnore)
        {
            _currentParameter = Math.Min(99_999, (_currentParameter < 0 ? 0 : _currentParameter * 10) + (c - '0'));
            _state = State.CsiParameter;
        }
        else if (c is ';' or ':' && _state != State.CsiIgnore)
        {
            AddParameter();
            _state = State.CsiParameter;
        }
        else if (c is '<' or '=' or '>' or '?')
        {
            if (_state == State.CsiEntry)
            {
                _privateMarker = c;
            }
            else
            {
                _state = State.CsiIgnore; // a marker after parameters is malformed
            }
        }
        else if (c is >= ' ' and <= '/')
        {
            _intermediates.Append(c);
        }
        else if (c is >= '@' and <= '~')
        {
            if (_state != State.CsiIgnore)
            {
                AddParameter();
                handler.CsiDispatch(_parameters, _privateMarker, _intermediates.ToString(), c);
            }

            _state = State.Ground;
        }
    }

    private void AddParameter()
    {
        if (_parameters.Count < MaxParameters)
        {
            _parameters.Add(_currentParameter);
        }

        _currentParameter = -1;
    }

    private void StepString(char c)
    {
        var terminated = c == '\x07' || (c == '\\' && _stringEscape);
        if (!terminated)
        {
            _stringEscape = c == '\x1b';
            if (!_stringEscape && _state == State.OscString && _string.Length < MaxStringLength)
            {
                _string.Append(c);
            }

            return;
        }

        if (_state == State.OscString)
        {
            handler.OscDispatch(_string.ToString());
        }

        _string.Clear();
        _stringEscape = false;
        _state = State.Ground;
    }
}

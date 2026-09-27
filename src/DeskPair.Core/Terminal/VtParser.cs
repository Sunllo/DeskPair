using System.Buffers;
using System.Text;

namespace DeskPair.Core.Terminal;

/// <summary>What the parser tells the screen. Every byte the shell writes ends up in exactly one of these.</summary>
public interface IVtHandler
{
    /// <summary>A printable character, already decoded from UTF-8.</summary>
    void Print(Rune rune);

    /// <summary>A C0 control: BEL, BS, HT, LF, VT, FF, CR, SO, SI and the rest.</summary>
    void Execute(byte control);

    /// <summary>
    /// CSI: <c>ESC [ params intermediates final</c>. Private markers (<c>?</c>, <c>&gt;</c>, <c>=</c>) arrive first
    /// in <paramref name="intermediates"/>. A missing parameter is 0; the handler applies the command's default.
    /// </summary>
    void CsiDispatch(ReadOnlySpan<int> parameters, ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>ESC: <c>ESC intermediates final</c>.</summary>
    void EscDispatch(ReadOnlySpan<byte> intermediates, byte final);

    /// <summary>OSC: the bytes between <c>ESC ]</c> and BEL or ST, undecoded.</summary>
    void OscDispatch(ReadOnlySpan<byte> data);
}

/// <summary>
/// Paul Williams's DEC ANSI state machine, byte-oriented, with a UTF-8 decoder in front of the ground
/// state. Bytes arrive in whatever pieces the pty and the wire cut them into, so every piece of state --
/// a half sequence, a half character -- survives between calls to <see cref="Feed"/>; feeding a stream
/// one byte at a time produces exactly what feeding it whole does, and a test holds every vector to that.
///
/// DCS, SOS, PM and APC strings are consumed and dropped: nothing this product draws needs them, and
/// consuming them correctly is what keeps a stray one from being printed as text.
/// </summary>
public sealed class VtParser
{
    private const int MaxParameters = 32;
    private const int MaxIntermediates = 4;
    private const int MaxOsc = 4096;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        DcsEntry,
        DcsPassthrough,
        OscString,
        SosPmApcString,
    }

    private readonly IVtHandler _handler;
    private readonly int[] _params = new int[MaxParameters];
    private readonly byte[] _intermediates = new byte[MaxIntermediates];
    private readonly byte[] _osc = new byte[MaxOsc];
    private readonly byte[] _utf8 = new byte[4];
    private State _state = State.Ground;
    private int _paramCount;
    private bool _paramStarted;
    private int _intermediateCount;
    private bool _intermediateOverflow;
    private int _oscLength;
    private int _utf8Length;
    private int _utf8Expected;
    private bool _stringEscape; // saw ESC inside a string; the next byte decides whether it was ST

    public VtParser(IVtHandler handler)
    {
        _handler = handler;
    }

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            Step(b);
        }
    }

    private void Step(byte b)
    {
        // A string's terminator is ESC \; the ESC is held back until the next byte says which it was.
        if (_stringEscape)
        {
            _stringEscape = false;
            if (b == (byte)'\\')
            {
                EndString();
                _state = State.Ground;
                return;
            }

            // Not ST: the string is abandoned and the ESC starts a new sequence.
            EndString(abandon: true);
            _state = State.Ground;
            Enter(State.Escape);
            Step(b);
            return;
        }

        if (_state != State.Ground && _utf8Length > 0)
        {
            _utf8Length = 0; // a sequence interrupted a character; the partial character is dropped
        }

        // "Anywhere" transitions.
        switch (b)
        {
            case 0x1B:
                if (_state is State.OscString or State.DcsPassthrough or State.SosPmApcString)
                {
                    _stringEscape = true;
                    return;
                }

                Enter(State.Escape);
                return;
            case 0x18 or 0x1A:
                if (_state is State.OscString or State.DcsPassthrough or State.SosPmApcString)
                {
                    EndString(abandon: true);
                }

                _handler.Execute(b);
                _state = State.Ground;
                return;
        }

        switch (_state)
        {
            case State.Ground:
                Ground(b);
                break;
            case State.Escape:
                if (IsControl(b))
                {
                    _handler.Execute(b);
                }
                else if (b is >= 0x20 and <= 0x2F)
                {
                    Collect(b);
                    _state = State.EscapeIntermediate;
                }
                else if (b == (byte)'[')
                {
                    Enter(State.CsiEntry);
                }
                else if (b == (byte)']')
                {
                    Enter(State.OscString);
                }
                else if (b == (byte)'P')
                {
                    Enter(State.DcsEntry);
                }
                else if (b is (byte)'X' or (byte)'^' or (byte)'_')
                {
                    Enter(State.SosPmApcString);
                }
                else if (b is >= 0x30 and <= 0x7E)
                {
                    _handler.EscDispatch(_intermediates.AsSpan(0, _intermediateCount), b);
                    _state = State.Ground;
                }

                // 0x7F and bytes above: ignored.
                break;
            case State.EscapeIntermediate:
                if (IsControl(b))
                {
                    _handler.Execute(b);
                }
                else if (b is >= 0x20 and <= 0x2F)
                {
                    Collect(b);
                }
                else if (b is >= 0x30 and <= 0x7E)
                {
                    _handler.EscDispatch(_intermediates.AsSpan(0, _intermediateCount), b);
                    _state = State.Ground;
                }

                break;
            case State.CsiEntry:
            case State.CsiParam:
                if (IsControl(b))
                {
                    _handler.Execute(b);
                }
                else if (b is >= (byte)'0' and <= (byte)'9')
                {
                    Digit(b);
                    _state = State.CsiParam;
                }
                else if (b is (byte)';' or (byte)':')
                {
                    // ':' is the sub-parameter separator (SGR 38:2:r:g:b); read as ';' it still names the colour.
                    NextParam();
                    _state = State.CsiParam;
                }
                else if (b is >= 0x3C and <= 0x3F)
                {
                    if (_state == State.CsiEntry)
                    {
                        Collect(b);
                        _state = State.CsiParam;
                    }
                    else
                    {
                        _state = State.CsiIgnore;
                    }
                }
                else if (b is >= 0x20 and <= 0x2F)
                {
                    Collect(b);
                    _state = State.CsiIntermediate;
                }
                else if (b is >= 0x40 and <= 0x7E)
                {
                    Dispatch(b);
                }

                break;
            case State.CsiIntermediate:
                if (IsControl(b))
                {
                    _handler.Execute(b);
                }
                else if (b is >= 0x20 and <= 0x2F)
                {
                    Collect(b);
                }
                else if (b is >= 0x30 and <= 0x3F)
                {
                    _state = State.CsiIgnore;
                }
                else if (b is >= 0x40 and <= 0x7E)
                {
                    Dispatch(b);
                }

                break;
            case State.CsiIgnore:
                if (IsControl(b))
                {
                    _handler.Execute(b);
                }
                else if (b is >= 0x40 and <= 0x7E)
                {
                    _state = State.Ground;
                }

                break;
            case State.DcsEntry:
                // Parameters and intermediates are consumed; the final byte hooks the passthrough, which is dropped.
                if (b is >= 0x40 and <= 0x7E)
                {
                    _state = State.DcsPassthrough;
                }

                break;
            case State.DcsPassthrough:
            case State.SosPmApcString:
                if (b == 0x07 && _state == State.SosPmApcString)
                {
                    _state = State.Ground;
                }

                break;
            case State.OscString:
                if (b == 0x07)
                {
                    EndString();
                    _state = State.Ground;
                }
                else if (b >= 0x20 || b == 0x09)
                {
                    if (_oscLength < MaxOsc)
                    {
                        _osc[_oscLength++] = b;
                    }
                }

                break;
        }
    }

    private void Ground(byte b)
    {
        if (b < 0x80)
        {
            if (_utf8Length > 0)
            {
                _utf8Length = 0;
                _handler.Print(Rune.ReplacementChar);
            }

            if (IsControl(b))
            {
                _handler.Execute(b);
            }
            else if (b != 0x7F)
            {
                _handler.Print(new Rune(b));
            }

            return;
        }

        // UTF-8: a lead byte says how many follow; a continuation byte without a lead, or a lead
        // without its continuations, is one replacement character, as every terminal shows it.
        if (_utf8Length == 0)
        {
            _utf8Expected = b switch
            {
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 0,
            };
            if (_utf8Expected == 0)
            {
                _handler.Print(Rune.ReplacementChar);
                return;
            }

            _utf8[_utf8Length++] = b;
            return;
        }

        if ((b & 0xC0) != 0x80)
        {
            _utf8Length = 0;
            _handler.Print(Rune.ReplacementChar);
            Ground(b);
            return;
        }

        _utf8[_utf8Length++] = b;
        if (_utf8Length == _utf8Expected)
        {
            _handler.Print(Rune.DecodeFromUtf8(_utf8.AsSpan(0, _utf8Length), out Rune rune, out _) == OperationStatus.Done ? rune : Rune.ReplacementChar);
            _utf8Length = 0;
        }
    }

    private static bool IsControl(byte b) => b is <= 0x17 or 0x19 or (>= 0x1C and <= 0x1F);

    private void Enter(State state)
    {
        _state = state;
        _paramCount = 0;
        _paramStarted = false;
        _pendingEmpty = false;
        _intermediateCount = 0;
        _intermediateOverflow = false;
        _oscLength = 0;
        Array.Clear(_params);
    }

    private void Collect(byte b)
    {
        if (_intermediateCount < MaxIntermediates)
        {
            _intermediates[_intermediateCount++] = b;
        }
        else
        {
            _intermediateOverflow = true;
        }
    }

    private void Digit(byte b)
    {
        if (!_paramStarted)
        {
            AddParam();
            _paramStarted = true;
            _pendingEmpty = false;
        }

        int i = _paramCount - 1;
        _params[i] = Math.Min(_params[i] * 10 + (b - '0'), 65535);
    }

    /// <summary>A separator: "1;2" is two parameters, "1;" is two with the second empty, ";1" two with the first empty.</summary>
    private void NextParam()
    {
        if (!_paramStarted)
        {
            AddParam(); // the empty parameter before this separator
        }

        _paramStarted = false;
        _pendingEmpty = true;
    }

    private void AddParam()
    {
        if (_paramCount < MaxParameters)
        {
            _params[_paramCount++] = 0;
        }
    }

    private bool _pendingEmpty;

    private void Dispatch(byte final)
    {
        if (_pendingEmpty)
        {
            AddParam(); // the parameter a trailing separator promised
            _pendingEmpty = false;
        }

        if (!_intermediateOverflow)
        {
            _handler.CsiDispatch(_params.AsSpan(0, _paramCount), _intermediates.AsSpan(0, _intermediateCount), final);
        }

        _state = State.Ground;
    }

    private void EndString(bool abandon = false)
    {
        if (_state == State.OscString && !abandon)
        {
            _handler.OscDispatch(_osc.AsSpan(0, _oscLength));
        }

        _oscLength = 0;
    }
}

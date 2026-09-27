using DeskPair.Protocol.Media;

namespace DeskPair.Core.Transport.Udp;

/// <summary>
/// The receiver's streams: one <see cref="FrameAssembler"/> per display being watched, and the rules for which
/// datagrams belong to one.
///
/// Until the session says which displays it is on, one stream is live at a time. A keyframe of another display
/// replaces it -- the host switched before the confirmation reached us -- and a delta of another display is
/// dropped, having nothing to decode against. Once the session says (<see cref="Expect(IReadOnlyCollection{byte})"/>),
/// exactly those streams are live and anything else is a straggler from a display it has left. That is the whole
/// difference between one display and several: the rules for a single display are the rules for a set of one.
/// </summary>
public sealed class StreamAssemblers
{
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<byte, FrameAssembler> _streams = new();
    private HashSet<byte>? _expected;
    private bool _hadStream;
    private uint _retiredGivenUp;
    private uint _retiredRecovered;

    public StreamAssemblers(TimeProvider time) => _time = time;

    /// <summary>Frames given up on every stream this session has had, for diagnostics.</summary>
    public uint FramesGivenUp
    {
        get
        {
            lock (_lock)
            {
                return _retiredGivenUp + (uint)_streams.Values.Sum(a => a.FramesGivenUp);
            }
        }
    }

    /// <summary>Shards rebuilt by FEC on every stream this session has had, for diagnostics.</summary>
    public uint ShardsRecovered
    {
        get
        {
            lock (_lock)
            {
                return _retiredRecovered + (uint)_streams.Values.Sum(a => a.ShardsRecovered);
            }
        }
    }

    /// <summary>True while any live stream has lost a frame and waits for a keyframe.</summary>
    public bool ReferenceBroken
    {
        get
        {
            lock (_lock)
            {
                foreach (FrameAssembler a in _streams.Values)
                {
                    if (a.ReferenceBroken)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>True while <paramref name="stream"/> has lost a frame and waits for a keyframe; false for a stream not arriving here.</summary>
    public bool IsBroken(byte stream)
    {
        lock (_lock)
        {
            return _streams.TryGetValue(stream, out FrameAssembler? a) && a.ReferenceBroken;
        }
    }

    /// <summary>Feeds one decrypted video shard (shard header followed by shard bytes) to the stream it belongs to.</summary>
    public void Accept(in MediaCommonHeader common, ReadOnlySpan<byte> plaintext)
    {
        if (!MediaShardHeader.TryRead(plaintext, out MediaShardHeader h) || plaintext.Length < MediaShardHeader.Size + h.ShardLength)
        {
            return;
        }

        lock (_lock)
        {
            if (!_streams.TryGetValue(h.Stream, out FrameAssembler? assembler))
            {
                if (_expected is not null)
                {
                    return; // a straggler from a display the session has left
                }

                if (_streams.Count > 0 && (common.Flags & MediaPacketFlags.KeyFrame) == 0)
                {
                    return; // a delta of some other display has nothing to decode against
                }

                // A keyframe of another display before the session has said which it is on: the host
                // switched, and the new display replaces the old one. Only a confirmed set keeps two.
                RetireAll();
                assembler = Add(h.Stream);
            }

            assembler.Accept(in common, in h, plaintext);
        }
    }

    /// <summary>The one display the session is now watching.</summary>
    public void Expect(byte stream) => Expect([stream]);

    /// <summary>The displays the session is now watching: frames of any other stream are dropped from here on.</summary>
    public void Expect(IReadOnlyCollection<byte> streams)
    {
        lock (_lock)
        {
            _expected = [.. streams];
            foreach (byte gone in _streams.Keys.Where(s => !_expected.Contains(s)).ToList())
            {
                Retire(gone);
            }

            // A stream confirmed before any of its frames arrived keeps whatever arrives next; one that is
            // already running (its keyframe beat the confirmation) keeps what it has.
            foreach (byte stream in _expected)
            {
                if (!_streams.ContainsKey(stream))
                {
                    Add(stream);
                }
            }
        }
    }

    /// <summary>Applies the give-up rules on every stream (call periodically even without traffic).</summary>
    public void Tick()
    {
        lock (_lock)
        {
            foreach (FrameAssembler a in _streams.Values)
            {
                a.Tick();
            }
        }
    }

    public bool TryDequeue(out AssembledFrame frame)
    {
        lock (_lock)
        {
            foreach (FrameAssembler a in _streams.Values)
            {
                if (a.TryDequeue(out frame))
                {
                    return true;
                }
            }
        }

        frame = null!;
        return false;
    }

    /// <summary>Fills <paramref name="into"/> with the live streams, for the per-stream reports.</summary>
    public void CopyStreams(List<FrameAssembler> into)
    {
        into.Clear();
        lock (_lock)
        {
            into.AddRange(_streams.Values);
        }
    }

    private FrameAssembler Add(byte stream)
    {
        var assembler = new FrameAssembler(_time, stream, startsBroken: _hadStream);
        _hadStream = true;
        _streams[stream] = assembler;
        return assembler;
    }

    private void RetireAll()
    {
        foreach (byte stream in _streams.Keys.ToList())
        {
            Retire(stream);
        }
    }

    private void Retire(byte stream)
    {
        FrameAssembler a = _streams[stream];
        _retiredGivenUp += a.FramesGivenUp;
        _retiredRecovered += a.ShardsRecovered;
        a.Discard();
        _streams.Remove(stream);
    }
}

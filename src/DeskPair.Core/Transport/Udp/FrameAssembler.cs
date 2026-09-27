using System.Buffers;
using DeskPair.Core.Transport.Udp.Fec;
using DeskPair.Protocol.Media;

namespace DeskPair.Core.Transport.Udp;

/// <summary>A frame rebuilt from its datagrams; <see cref="Return"/> the buffer after use.</summary>
public sealed class AssembledFrame
{
    internal AssembledFrame(byte[] buffer, int length, in MediaShardHeader header, bool keyFrame)
    {
        Buffer = buffer;
        Length = length;
        Stream = header.Stream;
        Codec = header.Codec;
        FrameSeq = header.FrameSeq;
        PtsMs = header.PtsMs;
        Width = header.Width;
        Height = header.Height;
        KeyFrame = keyFrame;
    }

    public byte[] Buffer { get; }
    public int Length { get; }
    public byte Stream { get; }
    public byte Codec { get; }
    public uint FrameSeq { get; }
    public long PtsMs { get; }
    public int Width { get; }
    public int Height { get; }
    public bool KeyFrame { get; }

    public ReadOnlySpan<byte> Data => Buffer.AsSpan(0, Length);

    public void Return() => ArrayPool<byte>.Shared.Return(Buffer);
}

/// <summary>
/// Receiver side of one stream (one display) of the media channel: collects shards per frame and FEC block,
/// repairs erasures with Reed-Solomon as soon as a block has k shards, and hands frames out in sequence order.
/// Frames that cannot be completed in time are given up; after a lost video frame later delta frames are
/// discarded until a keyframe arrives, and the caller asks the host for one.
///
/// Each of the host's displays numbers its frames from one, so everything here -- the "already delivered"
/// watermark, the counters the report sends back -- belongs to one stream and starts again with the next.
/// Which streams exist, and which datagrams belong to them, is <see cref="StreamAssemblers"/>' business; what
/// the link as a whole delivered is <see cref="LinkStats"/>'.
/// </summary>
public sealed class FrameAssembler
{
    private const int MaxPending = 8;
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StallAfter = TimeSpan.FromMilliseconds(60);

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly SortedDictionary<uint, PendingFrame> _pending = new();
    private readonly Queue<AssembledFrame> _ready = new();
    private uint _nextExpected;
    private bool _haveDelivered;
    private double _intervalMs = 1000.0 / 60;
    private long _lastPtsMs;

    /// <param name="time">The clock every give-up rule is measured against.</param>
    /// <param name="stream">The display whose frames this assembles; shards of any other are ignored.</param>
    /// <param name="startsBroken">
    /// True when there is nothing to decode against until this stream's first keyframe: a display switched to
    /// mid-session. The first stream of a session starts clean, so a lossy start does not ask for an extra keyframe.
    /// </param>
    public FrameAssembler(TimeProvider time, byte stream = 0, bool startsBroken = false)
    {
        _time = time;
        Stream = stream;
        ReferenceBroken = startsBroken;
    }

    public byte Stream { get; }

    public long FramesDelivered { get; private set; }

    public uint FramesGivenUp { get; private set; }

    public uint ShardsRecovered { get; private set; }

    public uint HighestDecodable { get; private set; }

    public uint LastFrameSeqReceived { get; private set; }

    /// <summary>True after a video frame was given up; cleared by the next keyframe. The channel asks for a refresh while set.</summary>
    public bool ReferenceBroken { get; private set; }

    public int PendingCount => _pending.Count;

    /// <summary>Feeds one decrypted video shard (shard header followed by shard bytes).</summary>
    public void Accept(in MediaCommonHeader common, ReadOnlySpan<byte> plaintext)
    {
        if (!MediaShardHeader.TryRead(plaintext, out MediaShardHeader h) || plaintext.Length < MediaShardHeader.Size + h.ShardLength)
        {
            return;
        }

        Accept(in common, in h, plaintext);
    }

    /// <summary>Feeds a shard whose header the caller has already read and checked.</summary>
    internal void Accept(in MediaCommonHeader common, in MediaShardHeader h, ReadOnlySpan<byte> plaintext)
    {
        if (h.Stream != Stream)
        {
            return;
        }

        lock (_lock)
        {
            AcceptCore(in common, in h, plaintext);
        }
    }

    /// <summary>Releases everything pending or ready: the stream is no longer being watched.</summary>
    public void Discard()
    {
        lock (_lock)
        {
            foreach (PendingFrame frame in _pending.Values)
            {
                frame.Discard();
            }

            _pending.Clear();
            while (_ready.TryDequeue(out AssembledFrame? ready))
            {
                ready.Return();
            }
        }
    }

    private void AcceptCore(in MediaCommonHeader common, in MediaShardHeader h, ReadOnlySpan<byte> plaintext)
    {
        LastFrameSeqReceived = Math.Max(LastFrameSeqReceived, h.FrameSeq);
        if (_haveDelivered && h.FrameSeq < _nextExpected)
        {
            return; // already delivered or given up
        }

        if (!_pending.TryGetValue(h.FrameSeq, out PendingFrame? frame))
        {
            if (_pending.Count >= MaxPending)
            {
                GiveUpOldest();
            }

            frame = new PendingFrame(h, (common.Flags & MediaPacketFlags.KeyFrame) != 0, _time.GetTimestamp());
            _pending[h.FrameSeq] = frame;
            if (_lastPtsMs != 0 && h.PtsMs > _lastPtsMs && h.PtsMs - _lastPtsMs < 200)
            {
                _intervalMs = 0.9 * _intervalMs + 0.1 * (h.PtsMs - _lastPtsMs);
            }

            _lastPtsMs = Math.Max(_lastPtsMs, h.PtsMs);
        }

        frame.LastShardSeen = _time.GetTimestamp();
        if (frame.AddShard(in h, plaintext.Slice(MediaShardHeader.Size, h.ShardLength)))
        {
            ShardsRecovered += frame.Recovered;
            frame.Recovered = 0;
        }

        Drain();
    }

    /// <summary>Applies the give-up rules to the head of the queue (call periodically even without traffic).</summary>
    public void Tick()
    {
        lock (_lock)
        {
            Drain();
        }
    }

    public bool TryDequeue(out AssembledFrame frame)
    {
        lock (_lock)
        {
            return _ready.TryDequeue(out frame!);
        }
    }

    private void Drain()
    {
        while (_pending.Count > 0)
        {
            KeyValuePair<uint, PendingFrame> head = _pending.First();
            PendingFrame frame = head.Value;
            if (!_haveDelivered)
            {
                _nextExpected = head.Key;
                _haveDelivered = true;
            }

            if (frame.IsComplete)
            {
                Deliver(head.Key, frame);
                continue;
            }

            // A complete keyframe further on makes everything before it irrelevant.
            if (_pending.Any(p => p.Key > head.Key && p.Value.IsComplete && p.Value.KeyFrame))
            {
                GiveUp(head.Key, frame);
                continue;
            }

            // A large frame paced over many milliseconds keeps arriving; judge it by the time since its last shard,
            // not since its first, so a keyframe in transit is not thrown away while it is still being delivered.
            TimeSpan age = _time.GetElapsedTime(frame.FirstSeen);
            TimeSpan stalled = _time.GetElapsedTime(frame.LastShardSeen);
            bool newerComplete = _pending.Any(p => p.Key > head.Key && p.Value.IsComplete);
            TimeSpan reorderWait = TimeSpan.FromMilliseconds(Math.Max(2 * _intervalMs, StallAfter.TotalMilliseconds));
            if (age > MaxAge || (newerComplete && stalled > reorderWait))
            {
                GiveUp(head.Key, frame);
                continue;
            }

            break; // wait for more shards
        }
    }

    private void Deliver(uint seq, PendingFrame frame)
    {
        _pending.Remove(seq);
        _nextExpected = seq + 1;
        HighestDecodable = Math.Max(HighestDecodable, seq);
        if (frame.KeyFrame)
        {
            ReferenceBroken = false;
        }

        if (ReferenceBroken)
        {
            frame.Discard(); // a delta after a lost frame would only corrupt the picture
            return;
        }

        _ready.Enqueue(frame.Finish());
        FramesDelivered++;
    }

    private void GiveUp(uint seq, PendingFrame frame)
    {
        _pending.Remove(seq);
        _nextExpected = seq + 1;
        HighestDecodable = Math.Max(HighestDecodable, seq);
        FramesGivenUp++;
        ReferenceBroken = true;
        frame.Discard();
    }

    private void GiveUpOldest()
    {
        KeyValuePair<uint, PendingFrame> head = _pending.First();
        GiveUp(head.Key, head.Value);
    }

    private sealed class PendingFrame
    {
        private readonly Block[] _blocks;
        private readonly int _frameLength;
        private readonly int _shardLength;
        private readonly MediaShardHeader _header;

        public PendingFrame(in MediaShardHeader h, bool keyFrame, long firstSeen)
        {
            _header = h;
            _frameLength = (int)h.FrameLength;
            _shardLength = h.ShardLength;
            _blocks = new Block[h.BlockCount];
            KeyFrame = keyFrame;
            FirstSeen = firstSeen;
            LastShardSeen = firstSeen;
        }

        public bool KeyFrame { get; }
        public long FirstSeen { get; }
        public long LastShardSeen { get; set; }
        public uint Recovered { get; set; }

        public bool IsComplete
        {
            get
            {
                foreach (Block? b in _blocks)
                {
                    if (b is null || !b.Decodable)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>Returns true when the shard completed a block through FEC repair (for the recovered counter).</summary>
        public bool AddShard(in MediaShardHeader h, ReadOnlySpan<byte> shard)
        {
            Block block = _blocks[h.BlockIndex] ??= new Block(h.K, h.M, _shardLength);
            if (block.K != h.K || block.M != h.M)
            {
                return false;
            }

            block.Add(h.ShardIndex, shard);
            if (block.Decodable && !block.Repaired)
            {
                int repaired = block.Repair();
                if (repaired > 0)
                {
                    Recovered += (uint)repaired;
                    return true;
                }
            }

            return false;
        }

        public AssembledFrame Finish()
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(_frameLength, 1));
            int offset = 0;
            foreach (Block? block in _blocks)
            {
                for (int i = 0; i < block!.K && offset < _frameLength; i++)
                {
                    int take = Math.Min(_shardLength, _frameLength - offset);
                    block.Shards[i]!.AsSpan(0, take).CopyTo(buffer.AsSpan(offset));
                    offset += take;
                }

                block.Release();
            }

            return new AssembledFrame(buffer, _frameLength, in _header, KeyFrame);
        }

        public void Discard()
        {
            foreach (Block? block in _blocks)
            {
                block?.Release();
            }
        }

        private sealed class Block
        {
            private readonly int _shardLength;
            private int _present;

            public Block(int k, int m, int shardLength)
            {
                K = k;
                M = m;
                _shardLength = shardLength;
                Shards = new byte[k + m][];
            }

            public int K { get; }
            public int M { get; }
            public byte[]?[] Shards { get; }
            public bool Repaired { get; private set; }

            public bool Decodable => _present >= K;

            public void Add(int index, ReadOnlySpan<byte> shard)
            {
                if (index >= Shards.Length || Shards[index] is not null)
                {
                    return;
                }

                byte[] buf = ArrayPool<byte>.Shared.Rent(_shardLength);
                shard.CopyTo(buf);
                Shards[index] = buf;
                _present++;
            }

            /// <summary>Rebuilds missing data shards; returns how many were rebuilt.</summary>
            public int Repair()
            {
                Repaired = true;
                int missing = 0;
                for (int i = 0; i < K; i++)
                {
                    if (Shards[i] is null)
                    {
                        missing++;
                    }
                }

                if (missing == 0)
                {
                    return 0;
                }

                var memories = new Memory<byte>[K + M];
                bool[] present = new bool[K + M];
                for (int i = 0; i < K + M; i++)
                {
                    present[i] = Shards[i] is not null;
                    if (!present[i] && i < K)
                    {
                        Shards[i] = ArrayPool<byte>.Shared.Rent(_shardLength);
                    }

                    memories[i] = Shards[i] is { } s ? new Memory<byte>(s, 0, _shardLength) : Memory<byte>.Empty;
                }

                // Parity slots that are missing stay empty; Decode only reads present rows.
                for (int i = K; i < K + M; i++)
                {
                    if (!present[i])
                    {
                        memories[i] = new Memory<byte>(ArrayPool<byte>.Shared.Rent(_shardLength), 0, _shardLength);
                    }
                }

                bool ok = ReedSolomon.Decode(memories, present, K);
                for (int i = K; i < K + M; i++)
                {
                    if (!present[i])
                    {
                        ArrayPool<byte>.Shared.Return(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memories[i], out ArraySegment<byte> seg) ? seg.Array! : []);
                    }
                }

                return ok ? missing : 0;
            }

            public void Release()
            {
                for (int i = 0; i < Shards.Length; i++)
                {
                    if (Shards[i] is { } s)
                    {
                        ArrayPool<byte>.Shared.Return(s);
                        Shards[i] = null;
                    }
                }
            }
        }
    }
}

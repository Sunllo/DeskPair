using System.Buffers;
using DeskPair.Core.Transport.Udp.Fec;
using DeskPair.Protocol;
using DeskPair.Protocol.Media;

namespace DeskPair.Core.Transport.Udp;

/// <summary>A datagram in a pooled buffer; call <see cref="Return"/> once it has been sent.</summary>
public readonly struct PooledDatagram(byte[] buffer, int length)
{
    public byte[] Buffer { get; } = buffer;

    public int Length { get; } = length;

    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);

    public void Return() => ArrayPool<byte>.Shared.Return(Buffer);
}

/// <summary>
/// Splits one encoded frame into fixed-size shards, groups them into Reed-Solomon blocks with parity from
/// the <see cref="FecPlanner"/>, and seals every shard into an encrypted datagram. Sender side only.
/// </summary>
public sealed class MediaPacketizer
{
    private readonly MediaCipher _cipher;
    private readonly FecPlanner _planner;
    private readonly byte[] _plain = new byte[MediaShardHeader.Size + ProtocolConstants.MaxShardBytes];

    public MediaPacketizer(MediaCipher cipher, FecPlanner planner)
    {
        _cipher = cipher;
        _planner = planner;
    }

    public long FramesPacketized { get; private set; }

    public long DatagramsProduced { get; private set; }

    public long ParityDatagrams { get; private set; }

    /// <summary>Datagrams for the frame, in send order; the caller owns the pooled buffers.</summary>
    public List<PooledDatagram> Packetize(byte stream, byte codec, uint frameSeq, long ptsMs, bool keyFrame, int width, int height, ReadOnlySpan<byte> data)
    {
        int shardLength = ProtocolConstants.MaxShardBytes;
        int totalData = Math.Max(1, (data.Length + shardLength - 1) / shardLength);
        FecPlan plan = _planner.Plan(totalData, keyFrame);
        var result = new List<PooledDatagram>(totalData + plan.Blocks * plan.ParityPerBlock);
        MediaPacketFlags baseFlags = keyFrame ? MediaPacketFlags.KeyFrame : MediaPacketFlags.None;

        int dataOffset = 0;
        int shardsLeft = totalData;
        for (int block = 0; block < plan.Blocks; block++)
        {
            int k = Math.Min(plan.DataPerBlock, shardsLeft);
            shardsLeft -= k;
            int m = _planner.ParityFor(k, keyFrame);

            // Data shards: slices of the frame, the last one zero-padded.
            var dataShards = new ReadOnlyMemory<byte>[k];
            byte[][] rented = new byte[k][];
            for (int i = 0; i < k; i++)
            {
                int take = Math.Min(shardLength, data.Length - dataOffset);
                byte[] buf = ArrayPool<byte>.Shared.Rent(shardLength);
                data.Slice(dataOffset, Math.Max(0, take)).CopyTo(buf);
                if (take < shardLength)
                {
                    buf.AsSpan(Math.Max(0, take), shardLength - Math.Max(0, take)).Clear();
                }

                dataOffset += Math.Max(0, take);
                rented[i] = buf;
                dataShards[i] = new ReadOnlyMemory<byte>(buf, 0, shardLength);
            }

            var parityShards = new Memory<byte>[m];
            byte[][] parityRented = new byte[m][];
            for (int i = 0; i < m; i++)
            {
                parityRented[i] = ArrayPool<byte>.Shared.Rent(shardLength);
                parityShards[i] = new Memory<byte>(parityRented[i], 0, shardLength);
            }

            ReedSolomon.Encode(dataShards, parityShards);

            for (int i = 0; i < k + m; i++)
            {
                bool parity = i >= k;
                bool last = block == plan.Blocks - 1 && i == k + m - 1;
                var header = new MediaShardHeader(stream, codec, (byte)block, (byte)plan.Blocks, frameSeq, ptsMs, (ushort)i, (byte)k, (byte)m, (ushort)shardLength, (uint)data.Length, (ushort)width, (ushort)height);
                header.Write(_plain);
                ReadOnlySpan<byte> shard = parity ? parityShards[i - k].Span : dataShards[i].Span;
                shard.CopyTo(_plain.AsSpan(MediaShardHeader.Size));
                MediaPacketFlags flags = baseFlags | (parity ? MediaPacketFlags.Parity : MediaPacketFlags.None) | (last ? MediaPacketFlags.LastShard : MediaPacketFlags.None);
                byte[] datagram = ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaxUdpDatagramBytes);
                int n = _cipher.Seal(MediaPacketType.Video, flags, _plain.AsSpan(0, MediaShardHeader.Size + shardLength), datagram, out _);
                result.Add(new PooledDatagram(datagram, n));
                if (parity)
                {
                    ParityDatagrams++;
                }
            }

            foreach (byte[] buf in rented)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            foreach (byte[] buf in parityRented)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        FramesPacketized++;
        DatagramsProduced += result.Count;
        return result;
    }
}

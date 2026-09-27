namespace DeskPair.Core.Transport.Udp.Fec;

/// <summary>
/// Systematic Reed-Solomon erasure code over GF(256) with a Cauchy generator: k data shards produce m parity
/// shards, and any k of the k + m shards recover the data. Shards within a block share one length.
/// </summary>
public static class ReedSolomon
{
    public const int MaxShards = 255;

    /// <summary>Generator coefficient for parity row <paramref name="row"/> (0..m-1) and data column <paramref name="col"/> (0..k-1).</summary>
    public static byte Coefficient(int k, int row, int col) => GaloisField.Inverse((byte)((k + row) ^ col));

    /// <summary>Computes <paramref name="parity"/> (m buffers) from <paramref name="data"/> (k buffers of equal length).</summary>
    public static void Encode(ReadOnlyMemory<byte>[] data, Memory<byte>[] parity)
    {
        int k = data.Length, m = parity.Length;
        if (k == 0 || k + m > MaxShards)
        {
            throw new ArgumentOutOfRangeException(nameof(data), $"k + m must be within 1..{MaxShards}.");
        }

        for (int row = 0; row < m; row++)
        {
            parity[row].Span.Clear();
            for (int col = 0; col < k; col++)
            {
                GaloisField.MultiplyAdd(parity[row].Span, data[col].Span, Coefficient(k, row, col));
            }
        }
    }

    /// <summary>
    /// Fills in the missing data shards. <paramref name="shards"/> holds k data then m parity buffers; entries in
    /// <paramref name="present"/> say which arrived. Returns false when fewer than k shards are present.
    /// Missing data buffers must be allocated (they are overwritten); missing parity shards are not rebuilt.
    /// </summary>
    public static bool Decode(Memory<byte>[] shards, ReadOnlySpan<bool> present, int k)
    {
        int total = shards.Length;
        int m = total - k;
        if (k <= 0 || m < 0 || present.Length != total)
        {
            throw new ArgumentException("Inconsistent shard layout.", nameof(shards));
        }

        int available = 0;
        for (int i = 0; i < total; i++)
        {
            if (present[i])
            {
                available++;
            }
        }

        bool anyMissingData = false;
        for (int i = 0; i < k; i++)
        {
            anyMissingData |= !present[i];
        }

        if (!anyMissingData)
        {
            return true;
        }

        if (available < k)
        {
            return false;
        }

        // Pick k present rows: data rows are identity rows, parity rows are generator rows.
        int[] rows = new int[k];
        int n = 0;
        for (int i = 0; i < total && n < k; i++)
        {
            if (present[i])
            {
                rows[n++] = i;
            }
        }

        byte[,] matrix = new byte[k, k];
        for (int r = 0; r < k; r++)
        {
            int source = rows[r];
            if (source < k)
            {
                matrix[r, source] = 1;
            }
            else
            {
                for (int c = 0; c < k; c++)
                {
                    matrix[r, c] = Coefficient(k, source - k, c);
                }
            }
        }

        byte[,] inverse = Invert(matrix, k);

        // data_j = sum_r inverse[j, r] * shard[rows[r]] for each missing j.
        for (int j = 0; j < k; j++)
        {
            if (present[j])
            {
                continue;
            }

            shards[j].Span.Clear();
            for (int r = 0; r < k; r++)
            {
                GaloisField.MultiplyAdd(shards[j].Span, shards[rows[r]].Span, inverse[j, r]);
            }
        }

        return true;
    }

    private static byte[,] Invert(byte[,] a, int n)
    {
        byte[,] work = new byte[n, 2 * n];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                work[r, c] = a[r, c];
            }

            work[r, n + r] = 1;
        }

        for (int col = 0; col < n; col++)
        {
            int pivot = -1;
            for (int r = col; r < n; r++)
            {
                if (work[r, col] != 0)
                {
                    pivot = r;
                    break;
                }
            }

            if (pivot < 0)
            {
                throw new InvalidOperationException("Singular decoding matrix.");
            }

            if (pivot != col)
            {
                for (int c = 0; c < 2 * n; c++)
                {
                    (work[col, c], work[pivot, c]) = (work[pivot, c], work[col, c]);
                }
            }

            byte inv = GaloisField.Inverse(work[col, col]);
            for (int c = 0; c < 2 * n; c++)
            {
                work[col, c] = GaloisField.Multiply(work[col, c], inv);
            }

            for (int r = 0; r < n; r++)
            {
                if (r == col || work[r, col] == 0)
                {
                    continue;
                }

                byte factor = work[r, col];
                for (int c = 0; c < 2 * n; c++)
                {
                    work[r, c] ^= GaloisField.Multiply(factor, work[col, c]);
                }
            }
        }

        byte[,] result = new byte[n, n];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                result[r, c] = work[r, n + c];
            }
        }

        return result;
    }
}

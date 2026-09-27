using System.Text;
using System.Threading.Channels;
using DeskPair.Platform.Abstractions.Terminal;

namespace DeskPair.Core.Testing;

/// <summary>
/// A shell that is only a shell in the ways the protocol can see: it echoes what it is sent, answers a
/// few commands, and exits when told. What the tests pin is the plumbing around it -- ids, limits,
/// credit, permission, the kill on the way out -- none of which needs a real pty.
/// </summary>
public sealed class FakeTerminalHost : ITerminalHost
{
    public bool IsAvailable { get; set; } = true;

    public string? UnavailableReason { get; set; }

    /// <summary>What <see cref="DescribeIdentity"/> and every started shell report.</summary>
    public string Identity { get; set; } = "root";

    /// <summary>When set, every start fails with this message.</summary>
    public string? RefuseStart { get; set; }

    public List<FakeTerminal> Started { get; } = [];

    public string DescribeIdentity(TerminalRunAs runAs) => runAs == TerminalRunAs.Highest ? Identity : "engine";

    public ValueTask<ITerminal> StartAsync(TerminalRunAs runAs, int columns, int rows, CancellationToken ct)
    {
        if (RefuseStart is { } why)
        {
            throw new TerminalStartException(why);
        }

        var terminal = new FakeTerminal(DescribeIdentity(runAs), columns, rows);
        lock (Started)
        {
            Started.Add(terminal);
        }

        return ValueTask.FromResult<ITerminal>(terminal);
    }
}

/// <summary>
/// Echoes input; a line "exit" ends it with code 0, "exit N" with code N, "spew N" writes N bytes of
/// output at once, "pause" stops echoing until "resume". Disposal ends it with code -1 like a killed
/// process.
/// </summary>
public sealed class FakeTerminal : ITerminal
{
    private readonly ChannelStream _output = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _line = new();
    private bool _paused;

    public FakeTerminal(string identity, int columns, int rows)
    {
        Identity = identity;
        Columns = columns;
        Rows = rows;
        Write($"fake shell ({identity})\r\n$ ");
    }

    public string Identity { get; }

    public string Shell => "/bin/fake";

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public List<TerminalSignalKind> Signals { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Everything the viewer typed, for a test to read back.</summary>
    public StringBuilder Typed { get; } = new();

    public Stream Output => _output;

    public Task<int> Exited => _exited.Task;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (Disposed)
        {
            throw new IOException("the fake shell has ended");
        }

        string text = Encoding.UTF8.GetString(data.Span);
        lock (Typed)
        {
            Typed.Append(text);
        }

        foreach (char c in text)
        {
            if (c is '\r' or '\n')
            {
                string command = _line.ToString();
                _line.Clear();
                Run(command);
            }
            else
            {
                _line.Append(c);
                if (!_paused)
                {
                    Write(c.ToString());
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Resize(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public void Signal(TerminalSignalKind signal)
    {
        lock (Signals)
        {
            Signals.Add(signal);
        }

        if (signal is TerminalSignalKind.Kill or TerminalSignalKind.Terminate or TerminalSignalKind.Hangup)
        {
            End(signal == TerminalSignalKind.Kill ? 137 : 143);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (!Disposed)
        {
            End(-1);
        }

        return ValueTask.CompletedTask;
    }

    private void Run(string command)
    {
        Write("\r\n");
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length > 0 ? parts[0] : string.Empty)
        {
            case "exit":
                End(parts.Length > 1 && int.TryParse(parts[1], out int code) ? code : 0);
                return;
            case "spew":
                int count = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 1000;
                byte[] chunk = new byte[Math.Min(count, 65536)];
                Array.Fill(chunk, (byte)'x');
                for (int written = 0; written < count; written += chunk.Length)
                {
                    _output.Post(chunk.AsSpan(0, Math.Min(chunk.Length, count - written)).ToArray());
                }

                break;
            case "pause":
                _paused = true;
                break;
            case "resume":
                _paused = false;
                break;
            case "":
                break;
            default:
                Write($"{parts[0]}: ok\r\n");
                break;
        }

        Write("$ ");
    }

    private void Write(string text)
    {
        if (!Disposed)
        {
            _output.Post(Encoding.UTF8.GetBytes(text));
        }
    }

    private void End(int code)
    {
        Disposed = true;
        _output.Complete();
        _exited.TrySetResult(code);
    }

    /// <summary>A read-only stream fed by chunks, ending when completed: what a pty's master side looks like to a reader.</summary>
    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private byte[]? _current;
        private int _offset;

        public void Post(byte[] chunk) => _chunks.Writer.TryWrite(chunk);

        public void Complete() => _chunks.Writer.TryComplete();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_current is null || _offset >= _current.Length)
            {
                try
                {
                    _current = await _chunks.Reader.ReadAsync(ct).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    return 0;
                }

                _offset = 0;
            }

            int n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

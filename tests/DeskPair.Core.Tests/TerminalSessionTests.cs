using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Session;
using DeskPair.Core.Terminal;
using DeskPair.Core.Testing;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

/// <summary>
/// The host's side of a terminal, against a shell that is only a shell in the ways the wire can see. What
/// is pinned here is everything that must be true before a real pty exists: ids, the limit, the credit
/// window, output before exit, and that every way a shell ends actually ends it.
/// </summary>
public class TerminalSessionTests
{
    private readonly FakeTerminalHost _host = new();
    private readonly List<(Message Message, MessagePriority Priority)> _sent = [];

    private TerminalSession Create(TerminalRunAs runAs = TerminalRunAs.Highest) =>
        new(_host, runAs, (m, p, _) =>
        {
            lock (_sent)
            {
                _sent.Add((m, p));
            }

            return ValueTask.CompletedTask;
        }, NullLogger.Instance);

    private static TerminalAction Open(int id, int columns = 80, int rows = 24) => new() { Open = new TerminalOpen { Id = id, Columns = (uint)columns, Rows = (uint)rows } };

    private static TerminalAction Input(int id, string text) => new() { Input = new TerminalInput { Id = id, Data = ByteString.CopyFromUtf8(text) } };

    private List<TerminalResponse> Responses()
    {
        lock (_sent)
        {
            return _sent.Select(s => s.Message.TerminalResponse).ToList();
        }
    }

    private string OutputText(int id) => string.Concat(Responses().Where(r => r.UnionCase == TerminalResponse.UnionOneofCase.Output && r.Output.Id == id).Select(r => r.Output.Data.ToStringUtf8()));

    private async Task<TerminalResponse> WaitForAsync(Func<TerminalResponse, bool> what, string why)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20); // generous: the full suite runs thirteen projects at once
        while (true)
        {
            TerminalResponse? hit = Responses().FirstOrDefault(what);
            if (hit is not null)
            {
                return hit;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Waited for {why}; got: {string.Join(", ", Responses().Select(r => r.UnionCase))}");
            }

            await Task.Delay(10);
        }
    }

    private async Task WaitUntilAsync(Func<bool> condition, string why)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20); // generous: the full suite runs thirteen projects at once
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Waited for " + why);
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Opening_starts_a_shell_as_the_configured_identity_and_says_so()
    {
        await using TerminalSession session = Create();
        string? announced = null;
        session.Opened += identity => announced = identity;

        await session.HandleAsync(Open(1, 100, 30), CancellationToken.None);

        TerminalResponse opened = await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened, "opened");
        opened.Opened.Id.ShouldBe(1);
        opened.Opened.Identity.ShouldBe("root");
        opened.Opened.Shell.ShouldBe("/bin/fake");
        _host.Started.ShouldHaveSingleItem().Columns.ShouldBe(100);
        _host.Started[0].Rows.ShouldBe(30);
        session.Opens.ShouldBe(1);
        session.Identity.ShouldBe("root");
        announced.ShouldBe("root");
        session.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_user_identity_is_what_the_configuration_asked_for()
    {
        await using TerminalSession session = Create(TerminalRunAs.User);

        await session.HandleAsync(Open(1), CancellationToken.None);

        (await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened, "opened")).Opened.Identity.ShouldBe("engine");
    }

    [Fact]
    public async Task Input_reaches_the_shell_and_output_comes_back_at_bulk_priority()
    {
        await using TerminalSession session = Create();
        await session.HandleAsync(Open(1), CancellationToken.None);
        await WaitUntilAsync(() => OutputText(1).Contains("$ "), "the prompt");

        await session.HandleAsync(Input(1, "hello\n"), CancellationToken.None);

        await WaitUntilAsync(() => OutputText(1).Contains("hello: ok"), "the answer");
        _host.Started[0].Typed.ToString().ShouldBe("hello\n");
        lock (_sent)
        {
            _sent.Where(s => s.Message.TerminalResponse.UnionCase == TerminalResponse.UnionOneofCase.Output).ShouldAllBe(s => s.Priority == MessagePriority.Bulk);
            _sent.Single(s => s.Message.TerminalResponse.UnionCase == TerminalResponse.UnionOneofCase.Opened).Priority.ShouldBe(MessagePriority.Control);
        }
    }

    [Fact]
    public async Task More_than_the_limit_is_refused_and_so_is_a_reused_id()
    {
        await using TerminalSession session = Create();
        for (int id = 1; id <= TerminalSession.MaxTerminals; id++)
        {
            await session.HandleAsync(Open(id), CancellationToken.None);
        }

        await WaitUntilAsync(() => Responses().Count(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened) == TerminalSession.MaxTerminals, "all opened");

        await session.HandleAsync(Open(TerminalSession.MaxTerminals + 1), CancellationToken.None);
        TerminalResponse refused = await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Error, "the refusal");
        refused.Error.Id.ShouldBe(TerminalSession.MaxTerminals + 1);
        refused.Error.Message.ShouldContain(TerminalSession.MaxTerminals.ToString());
        _host.Started.Count.ShouldBe(TerminalSession.MaxTerminals, "the fifth shell never started");

        await session.HandleAsync(Open(1), CancellationToken.None);
        await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Error && r.Error.Id == 1, "the duplicate refused");
        _host.Started.Count.ShouldBe(TerminalSession.MaxTerminals);
    }

    [Fact]
    public async Task A_host_without_a_terminal_answers_with_its_reason()
    {
        _host.IsAvailable = false;
        _host.UnavailableReason = "no pseudo-terminal on this platform";
        await using TerminalSession session = Create();

        await session.HandleAsync(Open(1), CancellationToken.None);

        (await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Error, "the refusal")).Error.Message.ShouldBe("no pseudo-terminal on this platform");
        _host.Started.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_start_failure_is_reported_and_frees_the_slot()
    {
        _host.RefuseStart = "the shell could not be started";
        await using TerminalSession session = Create();

        await session.HandleAsync(Open(1), CancellationToken.None);
        (await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Error, "the failure")).Error.Message.ShouldBe("the shell could not be started");
        session.Count.ShouldBe(0);

        _host.RefuseStart = null;
        await session.HandleAsync(Open(1), CancellationToken.None);
        await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened, "the retry");
    }

    /// <summary>The exit notice takes the same queue as output, so it cannot arrive before the last byte.</summary>
    [Fact]
    public async Task The_shell_exiting_is_reported_after_its_last_output()
    {
        await using TerminalSession session = Create();
        await session.HandleAsync(Open(1), CancellationToken.None);
        await WaitUntilAsync(() => OutputText(1).Contains("$ "), "the prompt");

        await session.HandleAsync(Input(1, "exit 7\n"), CancellationToken.None);

        TerminalResponse exit = await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Exit, "the exit");
        exit.Exit.Code.ShouldBe(7);
        exit.Exit.Reason.ShouldBe("the shell exited");
        List<TerminalResponse> all = Responses();
        all.FindLastIndex(r => r.UnionCase == TerminalResponse.UnionOneofCase.Output).ShouldBeLessThan(all.FindIndex(r => r.UnionCase == TerminalResponse.UnionOneofCase.Exit));
        OutputText(1).ShouldContain("exit 7");
        await WaitUntilAsync(() => session.Count == 0, "the slot freed");
    }

    [Fact]
    public async Task Closing_kills_the_shell_and_says_so()
    {
        await using TerminalSession session = Create();
        await session.HandleAsync(Open(1), CancellationToken.None);
        await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened, "opened");

        await session.HandleAsync(new TerminalAction { Close = new TerminalClose { Id = 1 } }, CancellationToken.None);

        _host.Started[0].Disposed.ShouldBeTrue();
        TerminalResponse exit = await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Exit, "the exit");
        exit.Exit.Reason.ShouldBe("closed by the viewer");
        session.Count.ShouldBe(0);
    }

    /// <summary>What a withdrawn permission and a dropped connection both call.</summary>
    [Fact]
    public async Task Close_all_ends_every_shell_with_the_reason_given()
    {
        await using TerminalSession session = Create();
        await session.HandleAsync(Open(1), CancellationToken.None);
        await session.HandleAsync(Open(2), CancellationToken.None);
        await WaitUntilAsync(() => Responses().Count(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened) == 2, "both opened");

        await session.CloseAllAsync("terminal permission was withdrawn");

        _host.Started.ShouldAllBe(t => t.Disposed);
        Responses().Where(r => r.UnionCase == TerminalResponse.UnionOneofCase.Exit).Select(r => r.Exit.Reason).ShouldBe(["terminal permission was withdrawn", "terminal permission was withdrawn"]);
        session.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Resize_and_signals_reach_the_shell()
    {
        await using TerminalSession session = Create();
        await session.HandleAsync(Open(1), CancellationToken.None);
        await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened, "opened");

        await session.HandleAsync(new TerminalAction { Resize = new TerminalResize { Id = 1, Columns = 120, Rows = 40 } }, CancellationToken.None);
        await session.HandleAsync(new TerminalAction { Signal = new TerminalSignal { Id = 1, Signal = TerminalSignal.Types.Kind.Interrupt } }, CancellationToken.None);
        (_host.Started[0].Columns, _host.Started[0].Rows).ShouldBe((120, 40));
        _host.Started[0].Signals.ShouldBe([TerminalSignalKind.Interrupt]);

        await session.HandleAsync(new TerminalAction { Signal = new TerminalSignal { Id = 1, Signal = TerminalSignal.Types.Kind.Kill } }, CancellationToken.None);
        (await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Exit, "the exit")).Exit.Code.ShouldBe(137);
    }

    /// <summary>
    /// The credit window: a viewer that stops acknowledging stops the flow, and an ack restarts it. Without
    /// this a `cat` of a large file on a slow link would be buffered in full on the host.
    /// </summary>
    [Fact]
    public async Task Output_waits_for_credit_and_resumes_when_acknowledged()
    {
        await using TerminalSession session = Create();
        await session.HandleAsync(Open(1), CancellationToken.None);
        await WaitUntilAsync(() => OutputText(1).Contains("$ "), "the prompt");
        int prompt = OutputText(1).Length;

        await session.HandleAsync(Input(1, "spew 600000\n"), CancellationToken.None);

        await WaitUntilAsync(() => OutputText(1).Length >= TerminalSession.CreditWindow - TerminalSession.OutputChunk, "the window to fill");
        await Task.Delay(200);
        int stalledAt = OutputText(1).Length;
        stalledAt.ShouldBeLessThanOrEqualTo(TerminalSession.CreditWindow, "nothing beyond the window without an ack");
        await Task.Delay(200);
        OutputText(1).Length.ShouldBe(stalledAt, "and it stays there");

        await session.HandleAsync(new TerminalAction { Ack = new TerminalAck { Id = 1, Bytes = (uint)stalledAt } }, CancellationToken.None);

        await WaitUntilAsync(() => OutputText(1).Length > stalledAt + TerminalSession.OutputChunk, "output resumed");

        // From here on, acknowledge the way the controller does: exactly what has arrived. An ack is a count
        // of bytes consumed, so acknowledging more than has arrived buys nothing -- a version of this test
        // that did so hung whenever the machine was busy enough for its ack to land early.
        int acked = stalledAt;
        int wanted = prompt + "spew 600000\r\n".Length + 600000;
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (OutputText(1).Length < wanted)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "all of the output to arrive");
            int arrived = OutputText(1).Length;
            if (arrived > acked)
            {
                await session.HandleAsync(new TerminalAction { Ack = new TerminalAck { Id = 1, Bytes = (uint)(arrived - acked) } }, CancellationToken.None);
                acked = arrived;
            }

            await Task.Delay(10);
        }

        lock (_sent)
        {
            _sent.Where(s => s.Message.TerminalResponse.UnionCase == TerminalResponse.UnionOneofCase.Output)
                .ShouldAllBe(s => s.Message.TerminalResponse.Output.Data.Length <= TerminalSession.OutputChunk);
        }
    }

    [Fact]
    public async Task Disposing_the_session_ends_every_shell()
    {
        TerminalSession session = Create();
        await session.HandleAsync(Open(1), CancellationToken.None);
        await WaitForAsync(r => r.UnionCase == TerminalResponse.UnionOneofCase.Opened, "opened");

        await session.DisposeAsync();

        _host.Started[0].Disposed.ShouldBeTrue();
        session.Count.ShouldBe(0);
        await session.HandleAsync(Open(2), CancellationToken.None);
        _host.Started.Count.ShouldBe(1, "nothing opens after disposal");
    }
}

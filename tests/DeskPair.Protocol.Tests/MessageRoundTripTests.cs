using Google.Protobuf;
using DeskPair.Protocol.Ipc;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Protocol.Tests;

public class MessageRoundTripTests
{
    [Fact]
    public void Rendezvous_message_round_trips()
    {
        var msg = new RendezvousMessage
        {
            PunchHoleResponse = new PunchHoleResponse
            {
                HostAddr = new SocketAddress { Ip = ByteString.CopyFrom(10, 0, 0, 1), Port = 21118 },
                NatType = NatType.NatAsymmetric,
                RelayServer = "relay.example:21117",
            },
        };
        var parsed = RendezvousMessage.Parser.ParseFrom(msg.ToByteArray());
        parsed.UnionCase.ShouldBe(RendezvousMessage.UnionOneofCase.PunchHoleResponse);
        parsed.PunchHoleResponse.HostAddr.Port.ShouldBe(21118u);
        parsed.PunchHoleResponse.HostAddr.Ip.ToByteArray().ShouldBe(new byte[] { 10, 0, 0, 1 });
    }

    /// <summary>
    /// The terminal's place in the protocol, pinned: the field numbers were allocated once and an old peer
    /// that meets a renumbered field reads a different message.
    /// </summary>
    [Fact]
    public void Terminal_messages_round_trip_and_keep_their_numbers()
    {
        var action = new Message { TerminalAction = new TerminalAction { Input = new TerminalInput { Id = 3, Data = ByteString.CopyFrom(0x03, 0xFF, (byte)'a') } } };
        var response = new Message { TerminalResponse = new TerminalResponse { Exit = new TerminalExit { Id = 3, Code = 137, Reason = "killed" } } };

        Message parsedAction = Message.Parser.ParseFrom(action.ToByteArray());
        parsedAction.UnionCase.ShouldBe(Message.UnionOneofCase.TerminalAction);
        parsedAction.TerminalAction.Input.Data.ToByteArray().ShouldBe(new byte[] { 0x03, 0xFF, (byte)'a' }, "bytes, not text: a pty splits UTF-8 anywhere");
        Message parsedResponse = Message.Parser.ParseFrom(response.ToByteArray());
        parsedResponse.TerminalResponse.Exit.Code.ShouldBe(137);

        Message.Descriptor.FindFieldByName("terminal_action").FieldNumber.ShouldBe(19);
        Message.Descriptor.FindFieldByName("terminal_response").FieldNumber.ShouldBe(20);
        ((int)Permission.PermTerminal).ShouldBe(5);
        ((int)ConnType.ConnTerminal).ShouldBe(2);
        TerminalOpen.Descriptor.Fields.InDeclarationOrder().Select(f => f.Name).ShouldBe(["id", "columns", "rows"], "no program, arguments, directory or environment cross the wire");
    }

    /// <summary>The numbers the multi-display plan allocated, held so nobody picks their own later.</summary>
    [Fact]
    public void Display_subscription_keeps_its_numbers()
    {
        var subscribe = new Message { Misc = new Misc { DisplaySubscription = new DisplaySubscription { Displays = { 0, 2 }, Focus = 2 } } };
        Message parsed = Message.Parser.ParseFrom(subscribe.ToByteArray());
        parsed.Misc.DisplaySubscription.Displays.ShouldBe([0, 2]);
        parsed.Misc.DisplaySubscription.Focus.ShouldBe(2);

        Misc.Descriptor.FindFieldByName("display_subscription").FieldNumber.ShouldBe(18);
        PeerInfo.Descriptor.FindFieldByName("multi_display").FieldNumber.ShouldBe(9);
    }

    [Fact]
    public void Virtual_display_keeps_its_numbers()
    {
        var add = new Message { Misc = new Misc { VirtualDisplayRequest = new VirtualDisplayRequest { Action = VirtualDisplayRequest.Types.Action.VdAdd, Resolution = new Resolution { Width = 1280, Height = 720 } } } };
        Message parsed = Message.Parser.ParseFrom(add.ToByteArray());
        parsed.Misc.VirtualDisplayRequest.Resolution.Width.ShouldBe(1280);

        Misc.Descriptor.FindFieldByName("virtual_display_request").FieldNumber.ShouldBe(19);
        DisplayInfo.Descriptor.FindFieldByName("virtual_display").FieldNumber.ShouldBe(11);
        ((int)VirtualDisplayRequest.Types.Action.VdAdd).ShouldBe(0);
        ((int)VirtualDisplayRequest.Types.Action.VdRemove).ShouldBe(1);
    }

    [Fact]
    public void Peer_message_round_trips_with_nested_oneof()
    {
        var msg = new Message
        {
            LoginRequest = new LoginRequest
            {
                MyId = "111222333",
                ConnType = ConnType.ConnFileTransfer,
                Options = new SessionOptions { CustomFps = 60, DisableAudio = BoolOption.BoYes },
            },
        };
        var parsed = Message.Parser.ParseFrom(msg.ToByteArray());
        parsed.UnionCase.ShouldBe(Message.UnionOneofCase.LoginRequest);
        parsed.LoginRequest.ConnType.ShouldBe(ConnType.ConnFileTransfer);
        parsed.LoginRequest.Options.CustomFps.ShouldBe(60);

        var misc = new Message { Misc = new Misc { VideoAck = new VideoAck { Display = 1, Seq = 42 } } };
        Message.Parser.ParseFrom(misc.ToByteArray()).Misc.UnionCase.ShouldBe(Misc.UnionOneofCase.VideoAck);
    }

    [Fact]
    public void Display_mode_messages_round_trip_and_an_old_display_info_still_parses()
    {
        var changed = new Message
        {
            Misc = new Misc
            {
                DisplaysChanged = new DisplaysChanged
                {
                    CurrentDisplay = 1,
                    Changed = 1,
                    Displays =
                    {
                        new DisplayInfo { Width = 1280, Height = 720, Name = "\\\\.\\DISPLAY1", Original = new Resolution { Width = 1920, Height = 1080 }, Modes = { new Resolution { Width = 1920, Height = 1080 }, new Resolution { Width = 1280, Height = 720 } } },
                    },
                },
            },
        };
        var parsed = Message.Parser.ParseFrom(changed.ToByteArray());
        parsed.Misc.UnionCase.ShouldBe(Misc.UnionOneofCase.DisplaysChanged);
        parsed.Misc.DisplaysChanged.Displays[0].Modes.Count.ShouldBe(2);
        parsed.Misc.DisplaysChanged.Displays[0].Original.Width.ShouldBe(1920);
        parsed.Misc.DisplaysChanged.Failure.ShouldBeEmpty();

        var request = new Message { Misc = new Misc { DisplayResolution = new DisplayResolution { Display = 0, Resolution = new Resolution { Width = 2560, Height = 1440, Scale = 2 } } } };
        var parsedRequest = Message.Parser.ParseFrom(request.ToByteArray());
        parsedRequest.Misc.DisplayResolution.Resolution.Scale.ShouldBe(2.0);

        // "Back to the original" is a request with no resolution at all.
        var restore = Message.Parser.ParseFrom(new Message { Misc = new Misc { DisplayResolution = new DisplayResolution { Display = 0 } } }.ToByteArray());
        restore.Misc.DisplayResolution.Resolution.ShouldBeNull();

        // What a 0.3.1 host sends: fields 1-8 only. It parses, with no modes and no original.
        var old = DisplayInfo.Parser.ParseFrom(new DisplayInfo { Width = 1920, Height = 1080, Scale = 1 }.ToByteArray());
        old.Modes.ShouldBeEmpty();
        old.Original.ShouldBeNull();
    }

    [Fact]
    public void Ipc_message_round_trips()
    {
        var msg = new IpcMessage { ApprovalDecision = new ApprovalDecision { ConnId = 7, Accept = true, Granted = { Permission.PermKeyboard, Permission.PermFile } } };
        var parsed = IpcMessage.Parser.ParseFrom(msg.ToByteArray());
        parsed.ApprovalDecision.Granted.ShouldBe([Permission.PermKeyboard, Permission.PermFile]);
    }

    [Fact]
    public void Unknown_message_case_is_none_for_empty_payload()
    {
        Message.Parser.ParseFrom(ReadOnlySpan<byte>.Empty).UnionCase.ShouldBe(Message.UnionOneofCase.None);
    }
}

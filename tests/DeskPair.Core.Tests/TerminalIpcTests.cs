using System.Reflection;
using DeskPair.Core.Ipc;
using DeskPair.Core.Services;
using DeskPair.Core.Terminal;
using DeskPair.Platform.Abstractions.Terminal;
using DeskPair.Protocol.Ipc;

namespace DeskPair.Core.Tests;

/// <summary>
/// No local program can open a shell. Under the Windows service the engine is SYSTEM and its IPC pipe
/// is the largest attack surface the product has; if any IPC message could reach a terminal, every
/// program on the machine would be one message away from a SYSTEM shell. The only way to a shell is a
/// viewer's own request over an authorized session. These pin that in the structure, so a future
/// "open terminal" convenience fails a test instead of shipping.
/// </summary>
public class TerminalIpcTests
{
    /// <summary>An IPC message about terminals may only ever be a read-only state push; nothing that asks for one.</summary>
    [Fact]
    public void No_ipc_message_is_a_terminal_command()
    {
        IEnumerable<string> fields = IpcMessage.Descriptor.Oneofs.SelectMany(o => o.Fields).Select(f => f.Name);

        fields.Where(f => f.Contains("terminal", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("_state", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    /// <summary>The bridge that serves IPC holds nothing that can start a shell: only a way to say whose shell it would be.</summary>
    [Fact]
    public void The_ipc_bridge_holds_no_way_to_start_a_shell()
    {
        Type[] reachable = [typeof(ITerminalHost), typeof(HostTerminalModule), typeof(TerminalSession), typeof(ITerminal)];
        MemberInfo[] members = typeof(HostIpcBridge).GetMembers(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (MemberInfo member in members)
        {
            Type? type = member switch
            {
                FieldInfo f => f.FieldType,
                PropertyInfo p => p.PropertyType,
                _ => null,
            };
            if (type is null)
            {
                continue;
            }

            reachable.ShouldNotContain(t => t.IsAssignableFrom(type) || (type.IsGenericType && type.GetGenericArguments().Any(a => t.IsAssignableFrom(a))), $"HostIpcBridge.{member.Name}");
        }

        typeof(HostIpcBridge).GetProperty(nameof(HostIpcBridge.TerminalIdentity))!.PropertyType.ShouldBe(typeof(Func<string>));
    }
}

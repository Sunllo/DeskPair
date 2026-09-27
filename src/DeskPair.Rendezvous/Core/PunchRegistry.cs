using System.Collections.Concurrent;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Rendezvous.Core;

/// <summary>
/// Controllers waiting for a host's punch answer, keyed by the controller's public address. The host
/// answers over a fresh TCP connection (PunchHoleSent / LocalAddr / RelayResponse) that names that address.
/// </summary>
public sealed class PunchRegistry
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RendezvousMessage>> _pending = new();

    public int PendingCount => _pending.Count;

    public TaskCompletionSource<RendezvousMessage> Register(string key)
    {
        var tcs = new TaskCompletionSource<RendezvousMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.AddOrUpdate(key, tcs, (_, old) =>
        {
            old.TrySetCanceled();
            return tcs;
        });
        return tcs;
    }

    public bool TryComplete(string key, RendezvousMessage answer)
        => _pending.TryRemove(key, out TaskCompletionSource<RendezvousMessage>? tcs) && tcs.TrySetResult(answer);

    public void Remove(string key, TaskCompletionSource<RendezvousMessage> expected)
    {
        if (_pending.TryGetValue(key, out TaskCompletionSource<RendezvousMessage>? current) && ReferenceEquals(current, expected))
        {
            _pending.TryRemove(new KeyValuePair<string, TaskCompletionSource<RendezvousMessage>>(key, expected));
        }
    }
}

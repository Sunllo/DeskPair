using Microsoft.Extensions.Logging;
using DeskPair.Core.Portal;

namespace DeskPair.Desktop.Services;

/// <summary>
/// What one sync did, for the log and for whatever wants to show it.
///
/// Both counts include folders as well as entries. They did not at first, and a sync that carried a newly
/// made empty group reported sending nothing — which is exactly the kind of quietly wrong number somebody
/// later uses to conclude the feature is broken.
/// </summary>
public sealed record SyncReport(bool Linked, int Pushed, int Pulled, string? Problem = null)
{
    public static readonly SyncReport NotLinked = new(false, 0, 0);

    public bool Changed => Pulled > 0;
}

/// <summary>
/// Keeps this machine's saved-computer list in step with the account's.
///
/// The whole exchange is one request: what changed here since the last confirmed revision, and the revision
/// itself. What comes back is everything that has happened in the book since, this machine's own writes
/// included, at the revisions the portal gave them.
///
/// **What changed here is a comparison, not a flag.** <see cref="DeviceBook.Synced"/> holds the book as the
/// portal last confirmed it; anything that differs is something to send, and anything missing from the live
/// list but present there is a deletion. Nothing in the rest of the app has to remember to mark an edit,
/// which is the failure this design is avoiding: a dirty flag is only ever as reliable as the least-careful
/// caller of the method that should have set it.
///
/// Only the personal book. Team books exist on the portal and are reachable by the same endpoint; showing
/// several lists is a change to the device page rather than to this file, and is not in here yet.
/// </summary>
public sealed class AddressBookSync(AccountLink link, ILogger<AddressBookSync> log, string? path = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Runs one exchange. Safe to call when unlinked, offline, or already running: all three do nothing and
    /// say so, rather than throwing at a caller that is usually a timer.
    /// </summary>
    public async Task<SyncReport> SyncAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // Already syncing. A second overlapping exchange would push a diff computed against a snapshot
            // the first one is about to replace.
            return SyncReport.NotLinked with { Linked = true };
        }

        try
        {
            return await RunAsync(ct).ConfigureAwait(false);
        }
        catch (PortalException e)
        {
            log.LogDebug(e, "Address book sync did not complete");
            return new SyncReport(true, 0, 0, e.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncReport> RunAsync(CancellationToken ct)
    {
        (string PortalUrl, string Token)? credential = await link.CredentialAsync(ct).ConfigureAwait(false);
        if (credential is null)
        {
            return SyncReport.NotLinked;
        }

        LinkState state = await link.RefreshAsync(ct).ConfigureAwait(false);
        if (!state.IsLinked)
        {
            return SyncReport.NotLinked;
        }

        PortalScope? personal = state.Scopes.FirstOrDefault(s => s.Kind == "personal");
        if (personal is null)
        {
            // The portal did not offer the account's own list, which it always should. Nothing useful to do
            // here, and guessing an id would be worse than waiting for the next round.
            log.LogWarning("The portal reported no personal address book for this device");
            return new SyncReport(true, 0, 0, null);
        }

        DeviceBook before = DeviceBook.Load(path);
        (List<EntryChange> entries, List<FolderChange> folders) = Diff(before);

        using var client = new PortalClient(credential.Value.PortalUrl);
        SyncResponse response = await client.SyncBookAsync(
            credential.Value.Token,
            new SyncRequest(personal.Kind, personal.Id, before.SyncRev, entries, folders),
            ct).ConfigureAwait(false);

        // Re-read: the user may have edited the list while the request was in flight, and the file rather
        // than the snapshot above is what has to be updated.
        DeviceBook after = Apply(DeviceBook.Load(path), response, [.. entries.Select(e => e.Target)]);
        after.Save(path);

        int sent = entries.Count + folders.Count;
        int received = response.Entries.Count + response.Folders.Count;
        if (sent > 0 || received > 0)
        {
            log.LogInformation(
                "Address book synced: sent {Sent}, received {Received}, now at revision {Rev}",
                sent, received, response.Rev);
        }

        return new SyncReport(true, sent, received);
    }

    /// <summary>What this machine has that the portal has not confirmed, and what it no longer has.</summary>
    internal static (List<EntryChange> Entries, List<FolderChange> Folders) Diff(DeviceBook book)
    {
        var confirmed = book.Synced.ToDictionary(d => d.Target, Comparer);
        var changes = new List<EntryChange>();

        foreach (SavedDevice device in book.Devices)
        {
            if (!confirmed.TryGetValue(device.Target, out SavedDevice? was) || !SameContent(was, device))
            {
                changes.Add(ToChange(device, deleted: false));
            }
        }

        var live = book.Devices.Select(d => d.Target).ToHashSet(Comparer);
        foreach (SavedDevice gone in book.Synced.Where(d => !live.Contains(d.Target)))
        {
            changes.Add(ToChange(gone, deleted: true));
        }

        var confirmedGroups = book.SyncedGroupNames.ToHashSet(Comparer);
        var liveGroups = book.GroupNames.ToHashSet(Comparer);
        var folders = new List<FolderChange>();
        folders.AddRange(book.GroupNames.Where(g => !confirmedGroups.Contains(g)).Select(g => new FolderChange(g, false)));
        folders.AddRange(book.SyncedGroupNames.Where(g => !liveGroups.Contains(g)).Select(g => new FolderChange(g, true)));

        return (changes, folders);
    }

    /// <summary>
    /// Folds what the portal sent into the book, and records it as confirmed.
    ///
    /// An entry that changed locally while the request was in flight is left alone: it was not in what we
    /// sent, so the portal's copy is older than what is on screen, and overwriting it would lose an edit the
    /// user watched themselves make. It stays different from the snapshot, so the next sync sends it.
    /// </summary>
    internal static DeviceBook Apply(DeviceBook book, SyncResponse response, IReadOnlyList<string> pushed)
    {
        var sent = pushed.ToHashSet(Comparer);
        var confirmed = book.Synced.ToDictionary(d => d.Target, Comparer);
        var devices = book.Devices.ToDictionary(d => d.Target, Comparer);

        foreach (EntryChange entry in response.Entries)
        {
            bool editedMeanwhile =
                !sent.Contains(entry.Target)
                && devices.TryGetValue(entry.Target, out SavedDevice? local)
                && (!confirmed.TryGetValue(entry.Target, out SavedDevice? was) || !SameContent(was, local));

            if (editedMeanwhile)
            {
                continue;
            }

            if (entry.Deleted)
            {
                devices.Remove(entry.Target);
                confirmed.Remove(entry.Target);
            }
            else
            {
                SavedDevice applied = FromChange(entry);
                devices[entry.Target] = applied;
                confirmed[entry.Target] = applied;
            }
        }

        var groups = book.GroupNames.ToHashSet(Comparer);
        var confirmedGroups = book.SyncedGroupNames.ToHashSet(Comparer);
        foreach (FolderChange folder in response.Folders)
        {
            if (folder.Deleted)
            {
                groups.Remove(folder.Name);
                confirmedGroups.Remove(folder.Name);
            }
            else
            {
                groups.Add(folder.Name);
                confirmedGroups.Add(folder.Name);
            }
        }

        return book with
        {
            Devices = [.. devices.Values],
            Synced = [.. confirmed.Values],
            GroupNames = [.. groups],
            SyncedGroupNames = [.. confirmedGroups],

            // Folded away is how this screen looks to this person, not what the list contains, so it is
            // never sent and never received. Two machines can legitimately disagree about it.
            CollapsedGroups = book.CollapsedGroups,
            SyncRev = response.Rev,
        };
    }

    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Everything that travels. <c>CollapsedGroups</c> is not here, and that is the point.</summary>
    private static bool SameContent(SavedDevice a, SavedDevice b) =>
        string.Equals(a.Alias, b.Alias, StringComparison.Ordinal)
        && string.Equals(a.Group, b.Group, StringComparison.Ordinal)
        && string.Equals(a.Note, b.Note, StringComparison.Ordinal)
        && string.Equals(a.Platform, b.Platform, StringComparison.Ordinal)
        && a.LastConnected == b.LastConnected;

    private static EntryChange ToChange(SavedDevice device, bool deleted) => new(
        device.Target,
        device.Alias,
        device.Group,
        device.Note,
        device.LastConnected == default ? 0 : device.LastConnected.ToUnixTimeMilliseconds(),
        device.Platform,
        deleted,
        0);

    private static SavedDevice FromChange(EntryChange entry) => new()
    {
        Target = entry.Target,
        Alias = entry.Alias,
        Group = entry.Folder,
        Note = entry.Note,
        LastConnected = entry.LastConnected == 0 ? default : DateTimeOffset.FromUnixTimeMilliseconds(entry.LastConnected),
        Platform = entry.Platform,
    };
}

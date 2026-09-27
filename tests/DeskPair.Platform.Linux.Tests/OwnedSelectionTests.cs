using Shouldly;
using DeskPair.Platform.Linux.Clipboard;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The selection's bookkeeping, which needs no display: what TARGETS should name, and whether a change is
/// ours or somebody else's.
/// </summary>
public sealed class OwnedSelectionTests
{
    [Fact]
    public void Targets_are_whatever_was_written_not_a_fixed_pair()
    {
        OwnedSelection owned = OwnedSelection.Of([(1, [1, 2]), (2, [3]), (7, [4])], takenAt: 99);

        owned.Targets.ShouldBe([(nint)1, (nint)2, (nint)7], ignoreOrder: true);
        owned.TakenAt.ShouldBe(99u);
        owned[2].ShouldBe([(byte)3]);
        owned[3].ShouldBeNull("a target nobody wrote must be refused, not answered with nothing");
    }

    [Fact]
    public void An_empty_write_owns_nothing()
    {
        OwnedSelection.Of([], 5).IsEmpty.ShouldBeTrue();
        OwnedSelection.Empty.Targets.ShouldBeEmpty();
    }

    [Fact]
    public void The_fingerprint_covers_every_target()
    {
        OwnedSelection a = OwnedSelection.Of([(1, "hello"u8.ToArray()), (2, "world"u8.ToArray())], 1);
        OwnedSelection same = OwnedSelection.Of([(2, "world"u8.ToArray()), (1, "hello"u8.ToArray())], 2);
        OwnedSelection differentPayload = OwnedSelection.Of([(1, "hello"u8.ToArray()), (2, "WORLD"u8.ToArray())], 1);
        OwnedSelection differentTarget = OwnedSelection.Of([(1, "hello"u8.ToArray()), (3, "world"u8.ToArray())], 1);

        // Order and timestamp are not content; the targets and their bytes are.
        same.Fingerprint.ShouldBe(a.Fingerprint);
        differentPayload.Fingerprint.ShouldNotBe(a.Fingerprint);
        differentTarget.Fingerprint.ShouldNotBe(a.Fingerprint);
    }

    [Fact]
    public void A_write_of_text_is_recognised_when_it_comes_back()
    {
        // This is what stops our own write being published as an external change: the loop hashes what it
        // read and compares it with what it wrote.
        byte[] text = "copied on this machine"u8.ToArray();
        OwnedSelection owned = OwnedSelection.Of([(1, text)], 0);

        OwnedSelection.Hash(text).ShouldNotBe(owned.Fingerprint, "the per-target hash is not the whole-selection one");
        OwnedSelection.Of([(1, text)], 0).Fingerprint.ShouldBe(owned.Fingerprint);
    }
}

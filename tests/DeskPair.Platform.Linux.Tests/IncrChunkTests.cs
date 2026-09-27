using Shouldly;
using DeskPair.Platform.Linux.Clipboard;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The arithmetic the whole incremental transfer hangs on, which needs no display.
/// </summary>
/// <remarks>
/// Getting this wrong is quiet in a way that matters: the error handler swallows every X error, so a chunk
/// too large for the server produces a paste that yields nothing, with no log line anywhere. A clipboard
/// image is always past the single-request limit, so this path is the normal case rather than an edge one.
/// </remarks>
public sealed class IncrChunkTests
{
    [Fact]
    public void A_server_without_big_requests_still_gets_a_usable_chunk()
    {
        // XExtendedMaxRequestSize answers zero when the extension is missing; the plain limit applies.
        int chunk = X11Clipboard.IncrChunkSize(extendedUnits: 0, plainUnits: 65_535);
        chunk.ShouldBeInRange(64 * 1024, 1024 * 1024);
    }

    [Fact]
    public void The_extended_limit_wins_when_the_server_offers_one()
    {
        // 4 MiB extended against a tiny plain limit: the result must come from the extended figure, which
        // here means the clamp ceiling rather than the 64 KiB floor.
        X11Clipboard.IncrChunkSize(extendedUnits: 4 * 1024 * 1024, plainUnits: 1_024)
            .ShouldBe(1024 * 1024);
    }

    [Fact]
    public void A_server_that_reports_nothing_is_not_taken_at_its_word()
    {
        // Both zero should not yield a zero chunk, which would loop for ever writing nothing.
        X11Clipboard.IncrChunkSize(extendedUnits: 0, plainUnits: 0).ShouldBeGreaterThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public void A_miserly_server_is_floored_rather_than_believed()
    {
        X11Clipboard.IncrChunkSize(extendedUnits: 0, plainUnits: 16).ShouldBe(64 * 1024);
    }

    [Fact]
    public void A_screenshot_sized_payload_needs_more_than_one_piece()
    {
        // The reason this code exists: three megabytes never fits in one write on any real server.
        int chunk = X11Clipboard.IncrChunkSize(extendedUnits: 0, plainUnits: 65_535);
        const int screenshot = 3 * 1024 * 1024;
        (screenshot / chunk).ShouldBeGreaterThan(1);
    }
}

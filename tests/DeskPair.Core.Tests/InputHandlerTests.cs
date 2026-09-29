using DeskPair.Core.Session.Host.Handlers;

namespace DeskPair.Core.Tests;

/// <summary>A click on the viewer's picture, put on the display as it is now.</summary>
public sealed class InputHandlerTests
{
    [Fact]
    public void A_point_on_the_old_size_is_scaled_onto_the_new_one() =>
        InputHandler.OntoDisplay(1279, 719, 1280, 720, 1920, 1080).ShouldBe((1919, 1079));

    [Fact]
    public void The_centre_stays_the_centre() =>
        InputHandler.OntoDisplay(640, 360, 1280, 720, 2560, 1440).ShouldBe((1281, 721));

    [Theory]
    [InlineData(0, 0)]      // an older viewer does not say
    [InlineData(1920, 1080)] // nothing changed
    [InlineData(1080, 1920)] // a rotated monitor sent unrotated, not a new size
    public void Otherwise_the_point_is_left_alone(int frameWidth, int frameHeight) =>
        InputHandler.OntoDisplay(100, 50, frameWidth, frameHeight, 1920, 1080).ShouldBe((100, 50));

    [Fact]
    public void A_point_is_kept_on_the_display() =>
        InputHandler.OntoDisplay(5000, -3, 1280, 720, 800, 600).ShouldBe((799, 0));
}

using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using DeskPair.Desktop.ViewModels.Settings;
using DeskPair.Desktop.Views;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// The sign-in window has to survive being built.
///
/// It did not. Its code-behind wrote its own InitializeComponent as AvaloniaXamlLoader.Load(this), copied
/// from the settings pages, where it is harmless because none of them names a control. Assigning the
/// x:Name fields is the other half of the method the XAML compiler generates, so the hand-written one
/// loaded the window and left every named control null. Nothing failed at build time and no test touched
/// a window, so the first thing that knew was a click, and the exception took the application down with
/// it.
///
/// [AvaloniaFact] runs the test on a headless platform's own UI thread, which is what building a window
/// needs: no display and no GPU, but the right thread.
/// </summary>
public class SignInWindowTests
{
    [AvaloniaFact]
    public void It_can_be_built_with_a_view_model()
    {
        var window = new SignInWindow(new AccountSettingsViewModel());

        window.DataContext.ShouldBeOfType<AccountSettingsViewModel>();
    }

    /// <summary>
    /// Every named control is in the window.
    ///
    /// The logical tree, not the visual one: nothing has been shown, so there are no visuals yet.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("EmailBox")]
    public void Its_named_controls_are_found(string name)
    {
        var window = new SignInWindow(new AccountSettingsViewModel());

        window.GetLogicalDescendants()
            .OfType<Control>()
            .ShouldContain(c => c.Name == name, $"{name} is not in the window");
    }

    /// <summary>
    /// And the code-behind's field points at it, which is the half that broke.
    ///
    /// Loading the XAML by hand builds the tree, names and all, so the test above would go on passing
    /// while every generated field stayed null -- exactly the state that crashed the application. Until
    /// the close button was removed, the constructor happened to touch a field and so fell over here on
    /// its own; nothing does now, so the field is read directly.
    ///
    /// Reflection, because the generated fields are internal to the app assembly. Reaching for it is the
    /// point: this is a test about the generated code being there, not about an API anybody calls.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("EmailBox")]
    public void Its_named_controls_reached_the_code_behinds_fields(string name)
    {
        var window = new SignInWindow(new AccountSettingsViewModel());

        FieldInfo? field = typeof(SignInWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);

        field.ShouldNotBeNull($"the XAML compiler generated no field for {name}");
        field.GetValue(window).ShouldNotBeNull(
            $"{name} was never assigned: the window is loading its XAML without the generated InitializeComponent");
    }
}

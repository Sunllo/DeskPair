using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DeskPair.Tools.Screenshots;

/// <summary>
/// The far side of the remote-session picture: a made-up desktop, drawn here and handed to the session as the
/// frames a host would send. It imitates no particular system -- a wallpaper, two plain windows and a clock --
/// because the picture is about DeskPair's window, not about whatever the other computer runs.
/// </summary>
internal static class MockDesktop
{
    /// <summary>The desktop as BGRA pixels, the layout a decoded video frame arrives in.</summary>
    public static byte[] Render(int width, int height)
    {
        var desktop = new Grid
        {
            Width = width,
            Height = height,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#16324F"), 0),
                    new GradientStop(Color.Parse("#2D6A78"), 0.6),
                    new GradientStop(Color.Parse("#C98F58"), 1),
                },
            },
        };

        desktop.Children.Add(AppWindow("Sales by region", 150, 110, 1000, 640, Figures()));
        desktop.Children.Add(AppWindow("Meeting notes", 1210, 210, 560, 440, Notes()));
        desktop.Children.Add(new Border
        {
            Height = 52,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Color.Parse("#D9101418")),
            Child = new TextBlock
            {
                Text = "14:32",
                Foreground = Brushes.White,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 28, 0),
            },
        });

        desktop.Measure(new Size(width, height));
        desktop.Arrange(new Rect(0, 0, width, height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(desktop);

        byte[] pixels = new byte[width * height * 4];
        unsafe
        {
            fixed (byte* p = pixels)
            {
                bitmap.CopyPixels(new PixelRect(0, 0, width, height), (nint)p, pixels.Length, width * 4);
            }
        }

        return pixels;
    }

    private static Border AppWindow(string title, double x, double y, double w, double h, Control content) => new()
    {
        Width = w,
        Height = h,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(x, y, 0, 0),
        CornerRadius = new CornerRadius(10),
        Background = Brushes.White,
        BoxShadow = BoxShadows.Parse("0 12 40 0 #66000000"),
        ClipToBounds = true,
        Child = new DockPanel
        {
            Children =
            {
                Docked(new Border
                {
                    Height = 42,
                    Background = new SolidColorBrush(Color.Parse("#F1F3F6")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#E1E4E9")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = new TextBlock
                    {
                        Text = title,
                        FontSize = 15,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = new SolidColorBrush(Color.Parse("#2A3038")),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(18, 0, 0, 0),
                    },
                }, Dock.Top),
                content,
            },
        },
    };

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static Control Figures()
    {
        string[][] rows =
        [
            ["Region", "Q1", "Q2", "Q3", "Change"],
            ["North", "1,240", "1,315", "1,402", "+6.6%"],
            ["South", "980", "1,020", "1,187", "+16.4%"],
            ["East", "1,105", "1,090", "1,164", "+6.8%"],
            ["West", "1,410", "1,388", "1,452", "+4.6%"],
            ["Total", "4,735", "4,813", "5,205", "+8.1%"],
        ];

        var table = new Grid { Margin = new Thickness(24, 20, 24, 0) };
        for (int c = 0; c < rows[0].Length; c++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition(c == 0 ? new GridLength(1.4, GridUnitType.Star) : GridLength.Star));
        }

        for (int r = 0; r < rows.Length; r++)
        {
            table.RowDefinitions.Add(new RowDefinition(new GridLength(38)));
            for (int c = 0; c < rows[r].Length; c++)
            {
                bool header = r == 0 || r == rows.Length - 1;
                var cell = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#E6E9EE")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = new TextBlock
                    {
                        Text = rows[r][c],
                        FontSize = 15,
                        FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = new SolidColorBrush(Color.Parse(c == 4 && r > 0 ? "#1E7F4F" : "#2A3038")),
                        HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(6, 0),
                    },
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                table.Children.Add(cell);
            }
        }

        // A bar per region, sized from the third quarter's figures.
        var bars = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 36,
            Height = 220,
            Margin = new Thickness(40, 28, 24, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        foreach ((string name, double value, string colour) in new[] { ("North", 1402.0, "#3B82C4"), ("South", 1187.0, "#4BA3A8"), ("East", 1164.0, "#E0A04A"), ("West", 1452.0, "#7A6CC8") })
        {
            bars.Children.Add(new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Spacing = 8,
                Children =
                {
                    new Border { Width = 88, Height = value / 1500 * 170, CornerRadius = new CornerRadius(6, 6, 0, 0), Background = new SolidColorBrush(Color.Parse(colour)) },
                    new TextBlock { Text = name, FontSize = 14, Foreground = new SolidColorBrush(Color.Parse("#5A6270")), HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
        }

        return new StackPanel { Children = { table, bars } };
    }

    private static Control Notes() => new TextBlock
    {
        Margin = new Thickness(22, 18),
        FontSize = 16,
        LineHeight = 28,
        Foreground = new SolidColorBrush(Color.Parse("#2A3038")),
        TextWrapping = TextWrapping.Wrap,
        Text = "Friday review\n\n• Q3 is up 8% on Q2; South leads\n• Move the storage server to the new rack\n• Reception PC: update printer drivers\n• Next review: 12 October",
    };
}

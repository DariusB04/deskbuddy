using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeskBuddy;

/// cardul: chenar rotunjit, titlu mic cu majuscule și conținutul dedesubt (Children), ca Card de pe Windows
sealed class Card : Border
{
    readonly StackPanel body = new() { Spacing = 2 };
    public string Title { get; }
    public Controls Children => body.Children;

    public Card(string title)
    {
        Title = title;
        body.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, Margin = new Thickness(0, 0, 0, 6) });
        Background = Ui.CardBg;
        BorderBrush = Ui.Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(18, 14, 18, 12);
        Margin = new Thickness(0, 0, 0, 14);
        Child = body;
    }
}

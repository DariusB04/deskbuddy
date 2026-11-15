using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace DeskBuddy;

/// Iconița: un DeskBuddy mic cu ecran și ochi, plus un punct de stare (ca pe Windows), desenată în cod.
static class Icons
{
    public static RenderTargetBitmap Bitmap(Color dot, int size = 64)
    {
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var g = bmp.CreateDrawingContext())
        using (g.PushTransform(Matrix.CreateScale(size / 32.0, size / 32.0)))
        {
            g.DrawRectangle(new SolidColorBrush(Color.FromRgb(235, 238, 240)), null, new Rect(2, 3, 28, 25), 6, 6);
            g.DrawRectangle(new SolidColorBrush(Color.FromRgb(25, 30, 36)), null, new Rect(5.5, 7, 21, 14), 3, 3);
            var eye = new SolidColorBrush(Color.FromRgb(110, 200, 255));
            g.DrawRectangle(eye, null, new Rect(10, 11, 3, 6));
            g.DrawRectangle(eye, null, new Rect(19, 11, 3, 6));
            g.DrawEllipse(new SolidColorBrush(dot), new Pen(new SolidColorBrush(Color.FromRgb(30, 30, 30)), 1.5), new Point(26, 25), 5, 5);
        }
        return bmp;
    }

    public static WindowIcon Window(Color dot)
    {
        using var ms = new MemoryStream();
        Bitmap(dot).Save(ms);
        ms.Position = 0;
        return new WindowIcon(ms);
    }
}

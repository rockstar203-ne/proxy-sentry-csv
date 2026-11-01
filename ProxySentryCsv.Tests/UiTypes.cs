// Color-only substitutes let the real profile model run without a desktop runtime.
namespace Windows.UI { public readonly record struct Color(byte A, byte R, byte G, byte B); }
namespace Microsoft.UI
{
    public static class ColorHelper
    {
        public static Windows.UI.Color FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
    }
    public static class Colors
    {
        public static Windows.UI.Color White => new(255, 255, 255, 255);
        public static Windows.UI.Color Transparent => new(0, 0, 0, 0);
    }
}
namespace Microsoft.UI.Xaml.Media
{
    public sealed class SolidColorBrush(Windows.UI.Color color) { public Windows.UI.Color Color { get; } = color; }
}

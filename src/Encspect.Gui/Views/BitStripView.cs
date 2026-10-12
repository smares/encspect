using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// The first bytes of the selected node, bit by bit from the most significant, with the node's
// bits marked and each byte's offset above it.
public sealed class BitStripView : Control
{
    public static readonly StyledProperty<BitStrip?> StripProperty =
        AvaloniaProperty.Register<BitStripView, BitStrip?>(nameof(Strip));

    private const double FontSize = 12.5;
    private const double LabelSize = 10.5;
    private const double GroupGap = 10;

    static BitStripView()
    {
        AffectsRender<BitStripView>(StripProperty);
        AffectsMeasure<BitStripView>(StripProperty);
    }

    public BitStripView()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public BitStrip? Strip
    {
        get => GetValue(StripProperty);
        set => SetValue(StripProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Strip is not { } strip)
        {
            return;
        }

        (double cell, double labelHeight, double bitHeight) = Metrics();
        Typeface typeface = Palette.Monospace(this);
        IBrush foreground = Palette.Foreground(this);
        IBrush secondary = Palette.Secondary(this);
        IBrush markFill = Palette.AccentFill(this, 1);
        IBrush onMark = Brushes.White;
        double groupWidth = (8 * cell) + GroupGap;
        for (int i = 0; i < strip.Bytes.Length; i++)
        {
            double x = i * groupWidth;
            Draw(context, Format.Hex(strip.Offset + i), x, 0, typeface, LabelSize, secondary);
            for (int bit = 0; bit < 8; bit++)
            {
                bool isMarked = strip.Marked[(i * 8) + bit];
                double cellX = x + (bit * cell);
                if (isMarked)
                {
                    context.FillRectangle(markFill, new Rect(cellX, labelHeight, cell - 1, bitHeight), 2);
                }

                char digit = ((strip.Bytes[i] >> (7 - bit)) & 1) == 1 ? '1' : '0';
                var text = new FormattedText(digit.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, isMarked ? onMark : foreground);
                context.DrawText(text, new Point(cellX + ((cell - 1 - text.Width) / 2), labelHeight + ((bitHeight - text.Height) / 2)));
            }
        }

        if (strip.IsCut)
        {
            Draw(context, "…", strip.Bytes.Length * groupWidth, labelHeight, typeface, FontSize, secondary);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Strip is not { } strip)
        {
            return default;
        }

        (double cell, double labelHeight, double bitHeight) = Metrics();
        return new Size((strip.Bytes.Length * ((8 * cell) + GroupGap)) + 16, labelHeight + bitHeight);
    }

    private (double Cell, double LabelHeight, double BitHeight) Metrics()
    {
        Typeface typeface = Palette.Monospace(this);
        var digit = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, FontSize, Brushes.Black);
        var label = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, LabelSize, Brushes.Black);
        return (Math.Ceiling(digit.Width + 7), Math.Ceiling(label.Height + 2), Math.Ceiling(digit.Height + 6));
    }

    private static void Draw(DrawingContext context, string text, double x, double y, Typeface typeface, double size, IBrush brush) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush), new Point(x, y));
}

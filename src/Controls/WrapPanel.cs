using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Soundeck.Controls;

/// <summary>
/// Responsive wrap panel for the sound card grid.
/// </summary>
public class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(8.0, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
            new PropertyMetadata(8.0, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, rowH = 0, totalH = 0;
        double hg = HorizontalSpacing, vg = VerticalSpacing;

        foreach (UIElement child in Children)
        {
            child.Measure(available);
            double w = child.DesiredSize.Width;
            double h = child.DesiredSize.Height;

            if (x + w > available.Width && x > 0)
            {
                totalH += rowH + vg;
                x = 0; rowH = 0;
            }
            x += w + hg;
            rowH = Math.Max(rowH, h);
        }
        totalH += rowH;
        return new Size(available.Width, totalH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double hg = HorizontalSpacing, vg = VerticalSpacing;
        var rows = new List<List<UIElement>>();
        var row = new List<UIElement>();
        double x = 0;

        foreach (UIElement child in Children)
        {
            double w = child.DesiredSize.Width;
            if (x + w > final.Width && row.Count > 0) { rows.Add(row); row = new(); x = 0; }
            row.Add(child);
            x += w + hg;
        }
        if (row.Count > 0) rows.Add(row);

        double y = 0;
        foreach (var r in rows)
        {
            double maxH = r.Max(c => c.DesiredSize.Height);
            x = 0;
            foreach (var child in r)
            {
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width + hg;
            }
            y += maxH + vg;
        }
        return final;
    }
}

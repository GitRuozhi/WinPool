using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinPool.App.Services;
using Windows.Globalization.NumberFormatting;

namespace WinPool_App.Controls;

/// <summary>Rejects non-digits before entry and clamps numbers while typing or pasting.</summary>
public static class IntegerTextInput
{
    private sealed class Bounds
    {
        public Func<long?>? Maximum;
        public long Minimum;
        public Func<long>? DynamicMinimum;
        public bool FillMaximum;
        public bool Updating;
    }

    private static readonly ConditionalWeakTable<TextBox, Bounds> TextBounds = new();
    private static readonly ConditionalWeakTable<NumberBox, object> NumberBoxes = new();

    public static void Attach(TextBox box, Func<long?>? maximum = null, long minimum = 1,
        bool fillMaximumOnLostFocus = false)
    {
        if (TextBounds.TryGetValue(box, out var existing))
        {
            existing.Maximum = maximum;
            existing.Minimum = minimum;
            existing.FillMaximum = fillMaximumOnLostFocus;
            return;
        }
        var bounds = new Bounds { Maximum = maximum, Minimum = minimum,
            FillMaximum = fillMaximumOnLostFocus };
        TextBounds.Add(box, bounds);
        box.BeforeTextChanging += (_, args) =>
        {
            if (!bounds.Updating) args.Cancel = !IntegerInputPolicy.ContainsOnlyDigits(args.NewText);
        };
        box.TextChanged += (_, _) => Normalize(box, bounds, false);
        box.LostFocus += (_, _) => Normalize(box, bounds, true);
    }

    public static void RefreshBounds(TextBox box, bool useMaximum = false)
    {
        if (!TextBounds.TryGetValue(box, out var bounds)) return;
        if (useMaximum && bounds.Maximum?.Invoke() is { } maximum)
            Replace(box, bounds, Math.Max(bounds.Minimum, maximum).ToString(CultureInfo.InvariantCulture));
        else Normalize(box, bounds, false);
    }

    // Existing read-only capacities may contain fractional GiB. Rendering an
    // exact fact must not pass through rules intended for the user's new input.
    public static void SetDisplayText(TextBox box, string text)
    {
        if (TextBounds.TryGetValue(box, out var bounds)) Replace(box, bounds, text);
        else box.Text = text;
    }

    private static void Normalize(TextBox box, Bounds bounds, bool completeEmpty)
    {
        if (bounds.Updating || box.IsReadOnly || !box.IsEnabled) return;
        Replace(box, bounds, IntegerInputPolicy.Clamp(box.Text, bounds.DynamicMinimum?.Invoke() ?? bounds.Minimum, bounds.Maximum?.Invoke(),
            completeEmpty, bounds.FillMaximum));
    }

    private static void Replace(TextBox box, Bounds bounds, string text)
    {
        if (box.Text == text) return;
        var caret = box.SelectionStart;
        bounds.Updating = true;
        try
        {
            box.Text = text;
            box.SelectionStart = Math.Min(caret, text.Length);
        }
        finally { bounds.Updating = false; }
    }

    public static void Attach(NumberBox box)
    {
        if (NumberBoxes.TryGetValue(box, out _)) return;
        NumberBoxes.Add(box, new object());
        box.NumberFormatter = new DecimalFormatter { FractionDigits = 0, IsGrouped = false };
        void Connect()
        {
            box.ApplyTemplate();
            if (FindTextBox(box) is { } input)
            {
                Attach(input, () => double.IsFinite(box.Maximum) && box.Maximum < long.MaxValue
                        ? (long)Math.Floor(box.Maximum) : long.MaxValue,
                    double.IsFinite(box.Minimum) ? (long)Math.Max(0, Math.Ceiling(box.Minimum)) : 0);
                TextBounds.GetValue(input, _ => new Bounds()).DynamicMinimum = () =>
                    double.IsFinite(box.Minimum) ? (long)Math.Max(0, Math.Ceiling(box.Minimum)) : 0;
            }
        }
        box.Loaded += (_, _) => Connect();
        box.ValueChanged += (_, _) =>
        {
            if (double.IsFinite(box.Value) && box.Value != Math.Floor(box.Value))
                box.Value = Math.Clamp(Math.Floor(box.Value), box.Minimum, box.Maximum);
        };
        if (box.IsLoaded) Connect();
    }

    private static TextBox? FindTextBox(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox text) return text;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
    }
}

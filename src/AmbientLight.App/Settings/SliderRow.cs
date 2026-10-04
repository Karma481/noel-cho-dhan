using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace AmbientLight.App.Settings;

/// <summary>
/// One compact settings row: label, slider and formatted value on a single line. Its template lives in
/// Themes/Controls.xaml.
/// </summary>
public sealed class SliderRow : Control
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SliderRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(SliderRow),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDisplayChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(SliderRow), new PropertyMetadata(0d));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SliderRow), new PropertyMetadata(100d));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(SliderRow), new PropertyMetadata(1d));

    public static readonly DependencyProperty ValueFormatProperty =
        DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(SliderRow), new PropertyMetadata("{0:0}", OnDisplayChanged));

    private static readonly DependencyPropertyKey FormattedValuePropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(FormattedValue), typeof(string), typeof(SliderRow), new PropertyMetadata("0"));

    public static readonly DependencyProperty FormattedValueProperty = FormattedValuePropertyKey.DependencyProperty;

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>Snapping interval and keyboard step.</summary>
    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    /// <summary>Composite format of the value, for example <c>{0:0} %</c>.</summary>
    public string ValueFormat
    {
        get => (string)GetValue(ValueFormatProperty);
        set => SetValue(ValueFormatProperty, value);
    }

    /// <summary>The value as shown at the end of the row.</summary>
    public string FormattedValue => (string)GetValue(FormattedValueProperty);

    private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (SliderRow)d;
        row.SetValue(FormattedValuePropertyKey, string.Format(CultureInfo.CurrentCulture, row.ValueFormat, row.Value));
    }
}

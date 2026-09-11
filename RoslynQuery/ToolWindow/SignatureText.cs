using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using RoslynQuery.ReferenceGraph;

namespace RoslynQuery.ToolWindow;

/// <summary>Renders a row's signature into a <see cref="TextBlock"/> as classified runs, since <c>Inlines</c> is not bindable.</summary>
public static class SignatureText
{
    /// <summary>Classification name to brush; null leaves a run's inherited foreground. Must not be replaced by a direct editor-service call, or rendering requires the editor assemblies to load.</summary>
    internal static Func<string, Brush> BrushResolver { get; set; }

    public static readonly DependencyProperty PartsProperty = DependencyProperty.RegisterAttached(
        "Parts", typeof(object), typeof(SignatureText), new PropertyMetadata(null, OnPartsChanged));

    public static object GetParts(DependencyObject element) => element.GetValue(PartsProperty);

    public static void SetParts(DependencyObject element, object value) => element.SetValue(PartsProperty, value);

    private static void OnPartsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (!(element is TextBlock block)) return;

        block.Inlines.Clear();
        if (!(e.NewValue is IReadOnlyList<SignaturePart> parts)) return;

        var resolve = BrushResolver;

        foreach (var part in parts)
        {
            var run = new Run(part.Text);

            var brush = resolve?.Invoke(SignatureClassification.NameFor(part.Kind));
            if (brush != null) run.Foreground = brush;
            if (SignatureClassification.IsEmphasised(part.Kind)) run.FontWeight = FontWeights.Bold;

            block.Inlines.Add(run);
        }
    }
}

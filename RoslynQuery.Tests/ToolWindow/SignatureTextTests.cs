using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using Microsoft.CodeAnalysis;

using RoslynQuery.ReferenceGraph;
using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

// No Visual Studio host and no editor assemblies here, so this also proves rendering a row never requires them.
public class SignatureTextTests
{
    private static readonly IReadOnlyList<SignaturePart> Parts =
    [
        new SignaturePart(SymbolDisplayPartKind.NamespaceName, "Outer"),
        new SignaturePart(SymbolDisplayPartKind.Punctuation, "."),
        new SignaturePart(SymbolDisplayPartKind.ClassName, "Ops"),
        new SignaturePart(SymbolDisplayPartKind.Punctuation, "."),
        new SignaturePart(SymbolDisplayPartKind.MethodName, "Plain"),
        new SignaturePart(SymbolDisplayPartKind.Punctuation, "()")
    ];

    private static T OnSta<T>(Func<T> body)
    {
        T result = default;
        Exception failure = null;

        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { failure = ex; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure != null) throw new InvalidOperationException("STA body failed", failure);
        return result;
    }

    [Fact]
    public void Parts_BecomeOneRunPerPartInOrder()
    {
        var texts = OnSta(() =>
        {
            var block = new TextBlock();
            SignatureText.SetParts(block, Parts);

            return block.Inlines.OfType<Run>().Select(r => r.Text).ToList();
        });

        Assert.Equal(Parts.Select(p => p.Text), texts);
    }

    [Fact]
    public void MemberNames_AreBoldAndEverythingElseIsNot()
    {
        var weights = OnSta(() =>
        {
            var block = new TextBlock();
            SignatureText.SetParts(block, Parts);

            return block.Inlines.OfType<Run>().Select(r => (r.Text, Bold: r.FontWeight == FontWeights.Bold)).ToList();
        });

        Assert.Equal(["Plain"], weights.Where(w => w.Bold).Select(w => w.Text));
    }

    [Fact]
    public void WithoutAResolver_RunsKeepTheInheritedForeground()
    {
        var unset = OnSta(() =>
        {
            var block = new TextBlock();
            SignatureText.SetParts(block, Parts);

            return block.Inlines.OfType<Run>().All(r => r.ReadLocalValue(TextElement.ForegroundProperty) == DependencyProperty.UnsetValue);
        });

        Assert.True(unset);
    }

    [Fact]
    public void AResolver_ColoursOnlyTheRunsItHasABrushFor()
    {
        var coloured = OnSta(() =>
        {
            SignatureText.BrushResolver = name => name == "method name" ? Brushes.Red : null;

            try
            {
                var block = new TextBlock();
                SignatureText.SetParts(block, Parts);

                return block.Inlines.OfType<Run>()
                    .Where(r => r.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue)
                    .Select(r => (r.Text, IsRed: ReferenceEquals(r.Foreground, Brushes.Red)))
                    .ToList();
            }
            finally
            {
                SignatureText.BrushResolver = null;
            }
        });

        Assert.Equal([("Plain", true)], coloured);
    }

    [Fact]
    public void ReplacingOrClearingTheParts_RebuildsTheRuns()
    {
        var counts = OnSta(() =>
        {
            var block = new TextBlock();
            SignatureText.SetParts(block, Parts);
            var first = block.Inlines.Count;

            SignatureText.SetParts(block, new List<SignaturePart> { new SignaturePart(SymbolDisplayPartKind.Text, "Uses") });
            var second = block.Inlines.Count;

            SignatureText.SetParts(block, null);
            return (first, second, third: block.Inlines.Count);
        });

        Assert.Equal((6, 1, 0), counts);
    }
}

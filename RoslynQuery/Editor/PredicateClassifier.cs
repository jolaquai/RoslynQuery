using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace RoslynQuery.Editor;

[Export(typeof(IClassifierProvider))]
[ContentType(PredicateContentTypes.Name)]
internal sealed class PredicateClassifierProvider : IClassifierProvider
{
#pragma warning disable 649
    [Import]
    internal IClassificationTypeRegistryService Registry;
#pragma warning restore 649

    public IClassifier GetClassifier(ITextBuffer textBuffer) =>
        textBuffer.Properties.GetOrCreateSingletonProperty(() => new PredicateClassifier(Registry));
}

/// <summary>Lexical classification only.</summary>
internal sealed class PredicateClassifier : IClassifier
{
    private readonly IClassificationType _keyword;
    private readonly IClassificationType _string;
    private readonly IClassificationType _comment;
    private readonly IClassificationType _number;
    private readonly IClassificationType _operator;
    private readonly IClassificationType _identifier;

    public PredicateClassifier(IClassificationTypeRegistryService registry)
    {
        _keyword = registry.GetClassificationType(PredefinedClassificationTypeNames.Keyword);
        _string = registry.GetClassificationType(PredefinedClassificationTypeNames.String);
        _comment = registry.GetClassificationType(PredefinedClassificationTypeNames.Comment);
        _number = registry.GetClassificationType(PredefinedClassificationTypeNames.Number);
        _operator = registry.GetClassificationType(PredefinedClassificationTypeNames.Operator);
        _identifier = registry.GetClassificationType(PredefinedClassificationTypeNames.Identifier);
    }

    public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged
    {
        add { }
        remove { }
    }

    public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
    {
        var result = new List<ClassificationSpan>();
        var snapshot = span.Snapshot;

        foreach (var token in SyntaxFactory.ParseTokens(snapshot.GetText()))
        {
            AddTrivia(result, snapshot, span, token.LeadingTrivia);

            var type = Map(token);
            if (type != null && token.Span.Length > 0) Add(result, snapshot, span, token.Span.Start, token.Span.Length, type);

            AddTrivia(result, snapshot, span, token.TrailingTrivia);
        }

        return result;
    }

    private void AddTrivia(List<ClassificationSpan> result, ITextSnapshot snapshot, SnapshotSpan requested, SyntaxTriviaList trivia)
    {
        foreach (var item in trivia)
        {
            if (!item.IsKind(SyntaxKind.SingleLineCommentTrivia) && !item.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;
            Add(result, snapshot, requested, item.Span.Start, item.Span.Length, _comment);
        }
    }

    private static void Add(List<ClassificationSpan> result, ITextSnapshot snapshot, SnapshotSpan requested, int start, int length, IClassificationType type)
    {
        if (type is null) return;

        var end = Math.Min(start + length, snapshot.Length);
        if (start >= end || end <= requested.Start || start >= requested.End) return;

        result.Add(new ClassificationSpan(new SnapshotSpan(snapshot, Span.FromBounds(start, end)), type));
    }

    private IClassificationType Map(SyntaxToken token)
    {
        var kind = token.Kind();
        if (SyntaxFacts.IsKeywordKind(kind)) return _keyword;

        switch (kind)
        {
            case SyntaxKind.StringLiteralToken:
            case SyntaxKind.CharacterLiteralToken:
            case SyntaxKind.InterpolatedStringTextToken:
            case SyntaxKind.SingleLineRawStringLiteralToken:
            case SyntaxKind.MultiLineRawStringLiteralToken:
                return _string;
            case SyntaxKind.NumericLiteralToken:
                return _number;
            case SyntaxKind.IdentifierToken:
                return _identifier;
            default:
                return SyntaxFacts.IsPunctuation(kind) ? _operator : null;
        }
    }
}

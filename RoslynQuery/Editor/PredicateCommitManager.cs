using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Threading;

using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

using VsData = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;

namespace RoslynQuery.Editor;

[Export(typeof(IAsyncCompletionCommitManagerProvider))]
[Name("RoslynQuery Predicate Commit")]
[ContentType(PredicateContentTypes.Name)]
internal sealed class PredicateCommitManagerProvider : IAsyncCompletionCommitManagerProvider
{
    public IAsyncCompletionCommitManager GetOrCreate(ITextView textView) =>
        textView.Properties.GetOrCreateSingletonProperty(() => new PredicateCommitManager());
}

/// <summary>Declares the commit characters and performs the commit itself, rather than risk a real-C#-editor commit manager grabbing it and replacing the wrong span.</summary>
internal sealed class PredicateCommitManager : IAsyncCompletionCommitManager
{
    // Roslyn's C# set without the space. In a one-line predicate box, committing on a space is
    // more often a surprise than a shortcut.
    private static readonly ImmutableArray<char> Characters = ImmutableArray.Create(
        '{', '}', '[', ']', '(', ')', '.', ',', ':', ';', '+', '-', '*', '/', '%',
        '&', '|', '^', '!', '~', '=', '<', '>', '?', '@', '#', '\'', '"', '\\');

    public IEnumerable<char> PotentialCommitCharacters => Characters;

    public bool ShouldCommitCompletion(IAsyncCompletionSession session, SnapshotPoint location, char typedChar, CancellationToken token) => true;

    public VsData.CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, VsData.CompletionItem item, char typedChar, CancellationToken token)
    {
        // session.ApplicableToSpan was observed widened here, eating the character that triggered the
        // session - recomputed from the live caret instead of trusted.
        var snapshot = buffer.CurrentSnapshot;
        var span = PredicateWord.At(snapshot, session.TextView.Caret.Position.BufferPosition.Position);
        var text = typedChar is '\0' or '\t' or '\n' ? item.InsertText : item.InsertText + typedChar;

        using (var edit = buffer.CreateEdit())
        {
            edit.Replace(span, text);
            edit.Apply();
        }

        return new VsData.CommitResult(true, VsData.CommitBehavior.SuppressFurtherTypeCharCommandHandlers);
    }
}

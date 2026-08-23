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

/// <summary>
/// Declares the commit characters and performs the commit itself. <c>IAsyncCompletionSession.ShouldCommit</c>
/// answers from the union over the registered managers, so with none of them present no typed
/// character can ever commit an item. <see cref="TryCommit"/> does the edit by hand - our content
/// type's base definition is "code", which also pulls in whatever generic-code commit managers the
/// real C# editor exports, and leaving the commit to "the" default let one of those grab it and
/// replace the wrong span, dropping the typed character (a '.' committing to "SyntaxFactoryAccessorList"
/// instead of "SyntaxFactory.AccessorList"). Owning the edit outright removes that ambiguity.
/// </summary>
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
        // session.ApplicableToSpan is not trustworthy here - something (likely another source pulled
        // in via our content type's "code" base) has been observed widening it to swallow the
        // character that triggered the session, e.g. eating the '.' in "SyntaxFactory.AccessorList".
        // PredicateWord.At against the live caret is the same computation the source used to open the
        // session in the first place, so recomputing it here is self-consistent regardless of what the
        // platform did to the tracked span in between.
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

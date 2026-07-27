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
/// Exists to declare the commit characters and nothing else. <c>IAsyncCompletionSession.ShouldCommit</c>
/// answers from the union over the registered managers, so with none of them present no typed
/// character can ever commit an item. <see cref="TryCommit"/> declines on purpose: the session's own
/// commit replaces the applicable span, which is right now that the span is kept in sync.
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

    public VsData.CommitResult TryCommit(IAsyncCompletionSession session, ITextBuffer buffer, VsData.CompletionItem item, char typedChar, CancellationToken token) =>
        VsData.CommitResult.Unhandled;
}

using System;
using System.Collections.Immutable;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

using RoslynQuery.Query;

using RoslynCompletion = Microsoft.CodeAnalysis.Completion;
using VsData = Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;

namespace RoslynQuery.Editor;

[Export(typeof(IAsyncCompletionSourceProvider))]
[Name("RoslynQuery Predicate Completion")]
[ContentType(PredicateContentTypes.Name)]
internal sealed class PredicateCompletionSourceProvider : IAsyncCompletionSourceProvider
{
    public IAsyncCompletionSource GetOrCreate(ITextView textView) =>
        textView.Properties.GetOrCreateSingletonProperty(() => new PredicateCompletionSource());
}

/// <summary>
/// Feeds VS's completion UI from Roslyn's public <see cref="RoslynCompletion.CompletionService"/>,
/// run against the wrapped predicate. Positions are shifted by the wrapper prefix length.
/// </summary>
internal sealed class PredicateCompletionSource : IAsyncCompletionSource
{
    private static readonly object SessionStateKey = new object();
    private static readonly object RoslynItemKey = new object();
    private static readonly VsData.CompletionContext Empty = new VsData.CompletionContext([]);

    private sealed class SessionState
    {
        public SessionState(Microsoft.CodeAnalysis.Document document, RoslynCompletion.CompletionService service)
        {
            Document = document;
            Service = service;
        }

        public Microsoft.CodeAnalysis.Document Document { get; }
        public RoslynCompletion.CompletionService Service { get; }
    }

    public VsData.CompletionStartData InitializeCompletion(VsData.CompletionTrigger trigger, SnapshotPoint triggerLocation, CancellationToken token)
    {
        var snapshot = triggerLocation.Snapshot;

        return new VsData.CompletionStartData(
            VsData.CompletionParticipation.ProvidesItems,
            new SnapshotSpan(snapshot, PredicateWord.At(snapshot, triggerLocation.Position)));
    }

    public async Task<VsData.CompletionContext> GetCompletionContextAsync(
        IAsyncCompletionSession session, VsData.CompletionTrigger trigger, SnapshotPoint triggerLocation, SnapshotSpan applicableToSpan, CancellationToken token)
    {
        var target = PredicateBufferContext.GetTarget(triggerLocation.Snapshot.TextBuffer);
        // Scaffolding has to match the mode the text will actually compile in, or a statement body
        // gets completed against "return <statements>;" and binds nothing.
        var text = triggerLocation.Snapshot.GetText();
        var source = PredicateTemplate.Build(target, PredicateCompiler.DetectMode(text), text, out var offset);

        var document = PredicateDocumentFactory.Create(source);
        if (document is null) return Empty;

        var service = RoslynCompletion.CompletionService.GetService(document);
        if (service is null) return Empty;

        var completions = await service
            .GetCompletionsAsync(document, offset + triggerLocation.Position, ToRoslynTrigger(trigger), cancellationToken: token)
            .ConfigureAwait(false);

        if (completions is null || completions.ItemsList.Count == 0) return Empty;

        session.Properties[SessionStateKey] = new SessionState(document, service);

        var items = completions.ItemsList.Select(item =>
        {
            var vsItem = new VsData.CompletionItem(item.DisplayText, this);
            vsItem.Properties[RoslynItemKey] = item;
            return vsItem;
        }).ToImmutableArray();

        return new VsData.CompletionContext(items);
    }

    public async Task<object> GetDescriptionAsync(IAsyncCompletionSession session, VsData.CompletionItem item, CancellationToken token)
    {
        if (!session.Properties.TryGetProperty(SessionStateKey, out SessionState state)) return null;
        if (!item.Properties.TryGetProperty(RoslynItemKey, out RoslynCompletion.CompletionItem roslynItem)) return null;

        var description = await state.Service.GetDescriptionAsync(state.Document, roslynItem, token).ConfigureAwait(false);
        return description?.Text;
    }

    private static RoslynCompletion.CompletionTrigger ToRoslynTrigger(VsData.CompletionTrigger trigger)
    {
        switch (trigger.Reason)
        {
            case VsData.CompletionTriggerReason.Insertion:
                return RoslynCompletion.CompletionTrigger.CreateInsertionTrigger(trigger.Character);
            case VsData.CompletionTriggerReason.Backspace:
            case VsData.CompletionTriggerReason.Deletion:
                return RoslynCompletion.CompletionTrigger.CreateDeletionTrigger(trigger.Character);
            default:
                return RoslynCompletion.CompletionTrigger.Invoke;
        }
    }
}

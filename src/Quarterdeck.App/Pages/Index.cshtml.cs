using Microsoft.AspNetCore.Mvc.RazorPages;
using Quarterdeck.Application;
using Quarterdeck.Presentation;

namespace Quarterdeck.App.Pages;

public sealed class IndexModel(ISimulationUseCases source) : PageModel
{
    public IReadOnlyList<SimulationWorkflowProjection> CurrentWork { get; private set; } = [];
    public int ActiveCount { get; private set; }
    public int AttentionCount { get; private set; }
    public int CompleteCount { get; private set; }

    public void OnGet()
    {
        var queue = source.ReadWorkQueue(new());
        ActiveCount = queue.Count(item => item.QueueGroup != WorkflowQueueGroup.Complete);
        AttentionCount = queue.Count(item => item.QueueGroup == WorkflowQueueGroup.NeedsAttention);
        CompleteCount = queue.Count(item => item.QueueGroup == WorkflowQueueGroup.Complete);
        CurrentWork = queue
            .Where(item => item.QueueGroup != WorkflowQueueGroup.Complete)
            .Take(5)
            .ToArray();
    }

    public string PersonName(string personReference) =>
        SyntheticPersonLabel.ForDisplay(personReference);
}

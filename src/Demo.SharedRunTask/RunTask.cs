using System.Text.Json;
using Demo.TaskRunner;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace Demo.SharedRunTask;

// The only custom Elsa activity in this option. No generated adapters.
public sealed class RunTask : CodeActivity
{
    [Input] public Input<string> TaskName { get; set; } = default!;
    [Input] public Input<JsonElement> Payload { get; set; } = default!;
    [Output] public Output<JsonElement> Result { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var registry = context.GetRequiredService<TaskRegistry>();
        var result = await registry.RunAsync(context.Get(TaskName)!, context.Get(Payload), context.CancellationToken);
        context.Set(Result, result);
    }
}

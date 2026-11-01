using Elsa.Extensions;
using Elsa.Workflows;

namespace Demo.DynamicElsa;

// The only runtime activity class, shared by every catalog entry.
public sealed class DynamicTaskActivity : CodeActivity
{
    // Type + Version identify the selected task, even though this class is shared.
    // The definition handles input reading, the business call, and output writing.
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context) =>
        context.GetRequiredService<TaskCatalog>().Find(Type, Version).ExecuteAsync(this, context);
}

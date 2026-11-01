# Option 3: Shared RunTask

**Status: Working runtime demo, not a final design choice.**

This keeps the option numbers from our comparison: Option 1 is generated adapters; Option 2 is dynamic activities (a separate approach); Option 3 is shared RunTask.

## Run it

```bash
./run-shared-demo.sh
```

Requires .NET SDK 9. The demo uses Elsa 3.8.0. It runs two scenarios and 12 checks. A failed check exits with an error. See [actual output](../shared-demo-output.txt).

## What changes from Option 1?

There is only **one custom Elsa activity class**. It has two configuration inputs, `TaskName` and `Payload`, and one output, `Result`. Payload is the single business-data input; its JSON object can contain many nested fields.

```text
Earlier Elsa steps
      ↓
RunTask(TaskName, Payload)
      ↓
Registry finds the registered task
      ↓
JSON → typed input → ExecuteAsync → JSON result
      ↓
Next Elsa step
```

There are no generated adapters and no separate mapping activity. Both options use the same unchanged `IAppTask<TInput, TOutput>` contracts and business tasks.

## Registration

At startup, the application registers each allowed name with its typed task. The demo builds a scoped registry using scoped task services:

```csharp
registry.Register("CreateInvoice.v1", invoiceTask);
registry.Register("PlanShipment.v1", shipmentTask);
```

The generic registration method learns the input and output types through `IAppTask<TInput, TOutput>`. Each handler deserializes its own input type. No type names from workflow data are used to load arbitrary classes.

Names include a version so flows can target a specific contract. Version migration itself is not implemented. Duplicate names fail during registration, and unknown names fail during execution.

## Scenario 1: Invoice from earlier steps

Built-in Elsa `SetVariable` steps supply a customer, order lines, and tax rules. These are sample lookup results, not real API calls.

The mapping is directly on RunTask's Payload input:

```csharp
new RunTask
{
    TaskName = new("CreateInvoice.v1"),
    Payload = new(context => Json(new
    {
        customer = customer.Get(context),
        lines = lines.Get(context),
        tax = tax.Get(context)
    })),
    Result = new(invoice)
}
```

`Json(...)` is the demo helper that builds a `JsonElement`. Elsa evaluates this input expression before executing the activity. The registry converts the resulting JSON into `InvoiceInput` and calls the task.

The customer includes an address. Lines are a list of objects. The result includes the same nested data and totals. Total: **₹4,130**.

## Scenario 2: Shipment from the invoice result

Another instance of the same RunTask activity selects `PlanShipment.v1`. Its payload combines:

| Field | Source |
|---|---|
| invoice | Full JSON output from the first RunTask |
| origin | Warehouse object from an earlier step |
| packages | List with dimensions and SKU lists from an earlier step |
| rules.maxWeightKg | Fixed value: 10 |
| rules.signatureRequired | Expression reading the invoice's grand total |

The expression reads the generic JSON output like this:

```csharp
invoice.Get(context)
    .GetProperty("totals")
    .GetProperty("grandTotal")
    .GetDecimal() > 4000m
```

The shared runner turns the combined JSON into `ShipmentInput`. The task still receives typed C# objects. Result: **two packages, 3 kg, signature required**, with the invoice's destination address.

## Validation demonstrated

The runner uses strict JSON settings: required constructor fields, non-null annotated members, no extra properties, and no string-to-number coercion.

Twelve checks cover:

- Both tasks execute without Elsa incidents.
- Correct invoice total.
- Shipment rule reads the previous output.
- Nested address, lists, and dimensions survive handover.
- Unknown task name.
- Missing required input field.
- Wrong type at `$.lines[0].quantity`.
- Invalid quantity caught by the business task.
- Null nested address.
- Unexpected input field.
- Duplicate registration.
- Cancellation before dispatch.

The six bad workflow inputs run through Elsa. Each check verifies that an incident contains the expected error and the following activity does not run.

## Files

| File | Role |
|---|---|
| `src/Demo.Tasks/Tasks.cs` | Existing engine-independent tasks; unchanged |
| `src/Demo.TaskRunner/TaskRegistry.cs` | Engine-independent typed dispatch and JSON conversion |
| `src/Demo.SharedRunTask/RunTask.cs` | One Elsa adapter for all tasks |
| `src/Demo.SharedRunTask/Program.cs` | Registration, mappings, flows, and checks |

## Benefits and limits

**Benefits:** No code generator, no per-task adapter, one small Elsa integration, and reusable typed business code.

**Limits:** Elsa sees generic JSON, so it does not automatically know each selected task's field types. A misspelled JSON output path is a runtime error. The task registry is not a Studio form or a full schema catalog.

The demo uses developer-written C# input expressions. It does not include a consultant-facing nested editor, publish-time mapping checks, or saved Studio expressions. Those need a task schema catalog and Studio integration.

Strict deserialization is not complete business validation. For example, list elements need explicit null checks where required. Before production, add full recursive validation, stable schemas, persistence/resume tests, and retry/idempotency rules. Source data and IDs are fixed samples; no external service is changed.

To switch workflow engines, keep the tasks and registry. Replace the RunTask adapter and rebuild the flows and mappings for the new engine.

# Option 2 Dynamic Elsa activities

Option 2 uses **one shared Elsa activity class to run many different business tasks**. Elsa learns each task's name, version, input fields and output type from a catalog and an activity provider.

For example, Elsa receives two separate activity descriptions — **Create invoice** and **Plan shipment** — but both execute through `DynamicTaskActivity`.

There is no Generator step and no adapter source file per task. “Dynamic” means the activity descriptions are supplied at runtime. The C# task code must still be built and deployed before it can run.

**Current status:** Working runtime demo on Elsa 3.8.0 and .NET 9. The demo authors flows in C#; a completed consultant-facing Studio editor is not included.

## 1 How the flow works

There are three stages: register the available tasks, build a flow with input mappings, then execute it.

### A When the application starts

**Step 1 — Register the business implementations.** In `src/Demo.DynamicElsa/Program.cs`, dependency injection connects each task contract to the implementation that should run:

```csharp
services.AddScoped<IAppTask<InvoiceInput, Invoice>, CreateInvoiceTask>();
services.AddScoped<IAppTask<ShipmentInput, Shipment>, PlanShipmentTask>();
```

This answers: **Which C# implementation executes this input/output contract?**

**Step 2 — List the available task identities in the catalog.** In `src/Demo.DynamicElsa/TaskCatalog.cs`, each entry has a stable name, display name, version, input type and output type:

```csharp
new TaskDefinition<InvoiceInput, Invoice>(
    "Demo.Tasks.CreateInvoice", "Create invoice", 1)
```

This answers: **Which task and version may a flow select, and what data does it need?** The catalog reads the input type's public constructor parameters. For `InvoiceInput`, those fields are `Customer`, `Lines` and `Tax`.

**Step 3 — Tell Elsa about those tasks.** `TaskActivityProvider` converts each catalog entry into an Elsa activity descriptor. A descriptor describes the task's identity and typed inputs/output, and tells Elsa how to construct the shared activity when loading a flow.

The demo registers the shared infrastructure once:

```csharp
services.AddSingleton<TaskCatalog>();
services.AddActivityProvider<TaskActivityProvider>();
```

After building the service provider and opening a scope, the console demo explicitly loads the descriptors into Elsa's registry:

```csharp
await registry.RefreshDescriptorsAsync(
    scope.ServiceProvider.GetRequiredService<TaskActivityProvider>());
```

Elsa now knows these two descriptions:

```text
Create invoice v1
  Customer, Lines, Tax → Invoice

Plan shipment v1
  Invoice, Origin, Packages, Rules → Shipment
```

**This describes available activities; it does not execute the business tasks.** No per-task C# activity class is created.

### B When a flow is built

The flow author selects the task and version, chooses where every input comes from, and chooses where the result is stored. The catalog does not infer these mappings.

The current demo does this in C#:

```csharp
var invoiceDefinition = catalog.Find("Demo.Tasks.CreateInvoice", 1);

var invoiceActivity = invoiceDefinition.Bind(
    new Dictionary<string, object>
    {
        ["Customer"] = new Input<Customer>(customer),
        ["Lines"] = new Input<List<Line>>(lines),
        ["Tax"] = new Input<TaxRules>(tax)
    },
    new Output<Invoice>(invoice));
```

Here, `customer`, `lines` and `tax` are Elsa variables populated by earlier steps. `invoice` is the destination variable. The activity is then placed in the workflow's activity list.

`Bind` checks field names, required bindings, each binding's `Input<T>` type and the output binding's type. It creates a `DynamicTaskActivity` with the selected task's `Type` and `Version`.

The bindings are stored in `SyntheticProperties`. **Synthetic** means a typed input described in metadata rather than a fixed property on the shared C# activity class. These are separate typed inputs, not one generic JSON payload. At this stage, bindings are stored; their values are read when the workflow runs.

### C When the workflow executes

```text
Earlier Elsa steps populate Customer, Lines and Tax variables
    ↓
Elsa reaches the Create invoice activity
    ↓
DynamicTaskActivity selects CreateInvoice v1 from the catalog
    ↓
Shared field readers evaluate the mapped Input<T> bindings
    ↓
Shared code constructs InvoiceInput(Customer, Lines, Tax)
    ↓
Dependency injection resolves CreateInvoiceTask
    ↓
ExecuteAsync(input, cancellationToken) runs the business logic
    ↓
The typed Invoice result is written to the invoice variable
    ↓
Later Elsa activities can read that result
```

Next, another instance of the **same shared activity class** selects `PlanShipment` v1. Its input mappings pass the earlier invoice, warehouse and packages, and construct `Rules` using a fixed weight limit plus the invoice total.

```text
Create invoice result + Warehouse + Packages + Rules
    ↓
DynamicTaskActivity selects PlanShipment v1
    ↓
Construct ShipmentInput → resolve PlanShipmentTask → ExecuteAsync
    ↓
Typed Shipment result → next workflow step
```

Nested addresses, lists and dimensions remain typed objects. No separate mapping activity is required. In the recorded demo, the invoice totals **INR 4,130**, and the shipment has **two packages, 3 kg, signature required**.

**What a consultant sees:** The provider supplies separate browsable activity descriptions under Business tasks. A friendly Studio editor for nested fields and lists still needs implementation and validation; the current provider's generic input UI hint does not supply that editor.

## 2 What to do when writing a new task

Example: add a task that calculates a discount from an invoice total and a percentage.

### Step 1 Write the plain C# task and its data contracts

Add the input record, output record and task implementation to the existing `Demo.Tasks` project, following its existing namespace and `IAppTask<TInput, TOutput>` contract:

```csharp
public record CalculateDiscountInput(decimal Total, decimal Rate);
public record DiscountResult(decimal Discount, decimal Payable);

public sealed class CalculateDiscountTask
    : IAppTask<CalculateDiscountInput, DiscountResult>
{
    public Task<DiscountResult> ExecuteAsync(
        CalculateDiscountInput input,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Total < 0m)
            throw new ArgumentException("Total must not be negative.");
        if (input.Rate < 0m || input.Rate > 1m)
            throw new ArgumentException("Rate must be between 0 and 1.");

        var discount = input.Total * input.Rate;
        return Task.FromResult(
            new DiscountResult(discount, input.Total - discount));
    }
}
```

The task has no Elsa dependency. Put business validation inside the task and test it directly. The current dynamic integration requires **exactly one public input constructor**; it exposes that constructor's parameters as activity inputs. Its field reader accepts null for nullable constructor parameters and rejects null for required parameters. Every field still needs an explicit binding, including nullable fields.

If the task lives in a new project instead, also add a project reference from the host so the contracts and implementation are available to it.

### Step 2 Add one catalog entry

Add this entry to `TaskCatalog.Definitions` in `src/Demo.DynamicElsa/TaskCatalog.cs`:

```csharp
new TaskDefinition<CalculateDiscountInput, DiscountResult>(
    "Demo.Tasks.CalculateDiscount", "Calculate discount", 1)
```

Use a unique, stable identity and version. The shared code reads `Total` and `Rate` automatically; no per-task field descriptor code is needed for this supported constructor pattern.

### Step 3 Register the implementation

Add this registration alongside the existing task registrations in `src/Demo.DynamicElsa/Program.cs`, before `BuildServiceProvider()`:

```csharp
services.AddScoped<
    IAppTask<CalculateDiscountInput, DiscountResult>,
    CalculateDiscountTask>();
```

Register any services the task constructor depends on as well.

**Both steps 2 and 3 are required.** The catalog makes the task available to the shared integration; dependency injection supplies the code that executes it. The current catalog does not automatically discover newly written task classes.

### Step 4 Build and deploy the updated application

For the local demo, run:

```bash
./run-dynamic-demo.sh
```

The script runs `dotnet run --project src/Demo.DynamicElsa`, which builds and starts the host. On startup, the existing provider refresh includes the new catalog entry and supplies a **Calculate discount v1** descriptor.

For a deployed application, build and deploy the updated task code, registrations and catalog to the server and every worker that may execute the task. Restart or refresh the registry through the host's supported lifecycle. The current demo does not implement automatic hot deployment.

**There is no Generator command to run, and no change is required to `DynamicTaskActivity` or `TaskActivityProvider` for this example.**

### Step 5 Use the task in a workflow and map its inputs

Adding a task to the catalog does not insert it into existing workflows. To run it after Create invoice, declare an Elsa result variable, add it to the enclosing sequence's `Variables`, and add the following activity after the invoice step:

```csharp
var discount = new Variable<DiscountResult>();
var discountDefinition = catalog.Find("Demo.Tasks.CalculateDiscount", 1);

var discountActivity = discountDefinition.Bind(
    new Dictionary<string, object>
    {
        ["Total"] = new Input<decimal>(
            context => invoice.Get(context)!.Totals.GrandTotal),
        ["Rate"] = new Input<decimal>(0.10m)
    },
    new Output<DiscountResult>(discount));
```

These snippets use the same imports and workflow context as the existing `Program.cs`. Insert `discountActivity` into the sequence's `Activities` after the invoice activity. `Total` comes from the previous result; `Rate` is fixed at 10 percent. Later steps can read the `discount` variable. For INR 4,130, this example returns a discount of **INR 413** and payable amount **INR 3,717**.

This is how the current C# demo is authored. Selecting and mapping the same task through Studio requires the additional Studio integration described above.

### Step 6 Validate the addition and preserve existing contracts

Check the task directly, then execute it through Elsa with the real mappings. Confirm the descriptor exposes `Total`, `Rate` and the expected result type. Check invalid values, missing or wrong-type bindings and cancellation. Test save/load and restart/resume for workflows that use it before relying on persistence in production.

For later contract changes, introduce a new catalog version and retain the old contract and implementation while saved flows still need them. The current dependency injection lookup uses the input/output contract pair, not the catalog version. If two versions use that same pair but need different implementations, version-specific resolution needs additional design; distinct contract types can keep the registrations separate.

### Files changed for this example

| Location | Change |
|---|---|
| `src/Demo.Tasks/` | Add input, output and task implementation |
| `src/Demo.DynamicElsa/TaskCatalog.cs` | Add the stable task identity and version |
| `src/Demo.DynamicElsa/Program.cs` | Register the task and add workflow input/output mappings |
| Task and workflow checks | Verify behavior, bindings and persistence requirements |

The shared activity and provider are reused. A new task requires task code, a catalog entry, dependency injection registration, a build/deployment, and workflow mappings wherever the task is used.

## Existing demo evidence

The [captured output](../dynamic-demo-output.txt) records **13 passing checks** covering results, nested handover, invalid data, direct task use, cancellation, descriptor fields, missing/wrong-type bindings, unknown versions, and same-process save/reload of a literal-input invoice flow. The discount example above is explanatory and has not been added to the application.

The existing save/reload check does not establish process restart, paused workflow resume, Studio save/reopen, or persisted C# lambda expressions. Before production, also add complete contract validation, publish-time mapping checks and idempotency for retried external writes.


## Automated Option 2 binding tests

Run the unit and Elsa integration tests with:

```bash
dotnet test tests/Demo.DynamicElsa.Tests
```

The 36 tests cover:

- Literal input values and typed results before and after Elsa definition save/load: string, byte, int, long, float, double, decimal, bool, DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan, Guid, enum, array, lists, nested lists, dictionaries, nested records, empty lists and a populated nullable value.
- Nullable reference and value fields set to null, including definition save/load. Required null values remain errors.
- Value sources: literals, workflow variables, inputs supplied in RunWorkflowOptions at workflow start, and C# delegate expressions. Runtime inputs include decimal values and a nested Customer object. Missing required runtime object input stops the flow.
- Three-task handover: CreateInvoice produces an Invoice; the second task takes only Invoice.Totals.GrandTotal; the third takes the second task's Value field and computes ten percent. The observed final result is INR 413.
- Missing bindings, unknown field names, incompatible input/output binding types, and the default dependency injection catalog registration.

The catalog now also accepts an explicit definition list so tests and other hosts can register their own task set. The default host retains the invoice and shipment tasks.

**DateTime caveat:** Elsa 3.8 literal save/load can convert UTC DateTime to local representation. The test verifies that the instant survives, rather than asserting that DateTime.Kind survives. Use DateTimeOffset when an explicit offset is part of the contract; its round-trip is tested separately.

This is a defined coverage matrix, not every possible .NET type or every combination of source and type. Studio JavaScript/C#/Liquid expression evaluators, saved delegate expressions, process restart and paused resume are not covered by this suite. HTTP callers may supply JSON rather than typed objects; transport-to-contract conversion also needs separate host-level tests.

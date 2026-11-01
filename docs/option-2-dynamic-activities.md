# Option 2: Dynamic Elsa activities

**Status: Working runtime demo on Elsa 3.8.0. Not a final design choice.**

## What “dynamic” means

We give Elsa a description of each task instead of generating a C# activity class for each task.

Think of a description as a task's label and form: its name, version, input fields, and output type. Elsa calls it an **activity descriptor**.

```text
Our task catalog
  ├─ Create invoice: Customer, Lines, Tax → Invoice
  └─ Plan shipment: Invoice, Origin, Packages, Rules → Shipment
             ↓
     One activity provider
             ↓
     Two Elsa activity descriptions
             ↓
     One shared activity class
             ↓
     The selected plain C# task
```

There are no generated adapter files and no hand-written wrapper per task. There is still one shared Elsa integration layer. Task code still implements `IAppTask<TInput, TOutput>` and has no Elsa dependency.

“Dynamic” does not mean Elsa writes or downloads task code. The task implementation must already be deployed on the server. Adding a new C# task still needs a build and deployment.

## How it would look to a consultant

The provider supplies these separate entries under **Business tasks**:

```text
Business tasks
  Create invoice
  Plan shipment
```

A conceptual settings view for Plan shipment:

```text
Plan shipment — version 1
  Invoice    ← Create invoice result
  Origin     ← Warehouse data
  Packages   ← Package list
  Rules      ← Weight limit + signature rule
  Result     → Shipment variable
```

This is a sketch, not a screenshot of a completed Studio integration. The demo supplies real descriptors and executes them, but does not launch Studio. Its generic input UI hint is not a nested-object form. Friendly field mapping and nested-list editing still need Studio work.

## The four pieces

| File | Job |
|---|---|
| `TaskCatalog.cs` | Lists allowed task types with stable names and versions; reads input record fields |
| `TaskActivityProvider.cs` | Creates each Elsa description, including typed synthetic inputs and outputs |
| `DynamicTaskActivity.cs` | The one shared activity; finds and runs the selected task |
| `Program.cs` | Registers services, builds flows, maps inputs, and runs checks |

**Synthetic input** means a field described in metadata rather than a C# property on the activity class. Elsa stores these bindings in `SyntheticProperties`. The description tells Elsa their real types and how to read them.

The provider's constructor restores the shared activity when Elsa loads a saved flow. The activity's `Type` and `Version` select the task; users cannot supply arbitrary .NET types to run.

The demo explicitly refreshes the provider into Elsa's registry because it is a small console host.

## Example registration

The catalog records the task's input and output types:

```csharp
new TaskDefinition<InvoiceInput, Invoice>(
    "Demo.Tasks.CreateInvoice", "Create invoice", 1)
```

Dependency injection connects that contract to the business code:

```csharp
services.AddScoped<IAppTask<InvoiceInput, Invoice>, CreateInvoiceTask>();
```

The shared code reads `InvoiceInput`'s constructor fields and describes Customer, Lines, and Tax. This demo requires exactly one public input constructor.

## Where values come from

The flow author chooses the source. Descriptors describe what a task needs; they do not guess where to get it.

The developer-written mapping in this demo looks like:

```csharp
invoiceDefinition.Bind(new Dictionary<string, object>
{
    ["Customer"] = new Input<Customer>(customer),
    ["Lines"] = new Input<List<Line>>(lines),
    ["Tax"] = new Input<TaxRules>(tax)
}, new Output<Invoice>(invoice))
```

These are separate typed inputs, not one JSON input. At runtime, Elsa evaluates the bindings. Shared code builds `InvoiceInput`, calls the task, and writes its typed result. Nested addresses, line lists, and dimensions stay as typed objects.

The shipment Rules input builds an object directly from a fixed limit and the earlier invoice total. It does not add another activity.

## Demonstrated results

Run from the project folder:

```bash
./run-dynamic-demo.sh
```

- Invoice: customer with address + item list + tax rules → **₹4,130**.
- Shipment: invoice + warehouse + packages with dimensions and SKU lists → **two packages, 3 kg, signature required**.
- Bad quantity stops the flow and names `Lines[1].Quantity`.

**13 checks pass.** They cover both task results, nested handover, invalid data, cancellation, direct task use without Elsa, descriptor fields, missing/wrong-type bindings, unknown versions, and save/reload of a literal-input invoice flow.

The save/reload check first lets Elsa assign IDs to the flow, saves the definition with Elsa's serializer, reloads it, and runs it again in the same process. It verifies nested data and dynamic bindings. It does not test a process restart, paused workflow resume, Studio save/reopen, or persisted C# lambda expressions.

See [actual output](../dynamic-demo-output.txt).

## Compared with Option 1

| Question | Option 1: Generated adapters | Option 2: Dynamic activities |
|---|---|---|
| Per-task adapter source? | Generated | None |
| Runtime activity classes? | One per task | One shared class |
| How Elsa learns the fields | Typed class properties | Provider descriptors |
| How bindings are checked | C# types help catch errors at compile time | Shared binding checks plus runtime checks |
| Extra integration work | Generator and build step | Provider, descriptors, construction, and serialization |
| Business tasks depend on Elsa? | No | No |
| Friendly nested Studio editor included? | No | No |

## Before production

Keep names and versions stable. Keep old versions while saved flows still use them. Install the catalog and implementations on every worker.

Add proper task descriptions, field schemas, publish-time validation, Studio editing tests, restart/resume tests, and duplicate-write protection before retries. The demo's business validation and constructor support are intentionally limited; this is not a complete task framework.

## Source

Elsa documents providers, descriptors, synthetic activity use cases, and version rules in its [Activity Type Providers guide](https://docs.elsaworkflows.io/extensibility/activity-type-providers). The runtime checks here use the installed Elsa 3.8.0 package.

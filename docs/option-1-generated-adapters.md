# Option 1: Generated Elsa adapters

**Status: Working demo; an option for review, not a final design choice.**

## What we want

Developers write tasks. Consultants use those tasks to build business flows. Developers manage technical flows.

Flows can stay in Elsa. If we change engines later, we want to keep our task code. We do not want to write an Elsa wrapper for every task by hand.

## The design

Each task follows our own interface:

```csharp
public interface IAppTask<TInput, TOutput>
{
    Task<TOutput> ExecuteAsync(
        TInput input,
        CancellationToken cancellationToken);
}
```

This interface belongs to our application, not Elsa. It is how the demo's generator finds tasks and learns their input and output types. Another design could use attributes instead.

```text
Before running:
Task types → Generator → Elsa adapter source code → Compile

When running:
Elsa flow → Generated adapter → Our task → Result back to Elsa
```

The generator does not change the task. It creates a separate adapter beside it. For example, `CreateInvoiceTask` gets a generated `CreateInvoiceActivity`.

The adapter reads Elsa inputs, builds the task's input object, resolves the task through dependency injection, calls `ExecuteAsync`, and sets the Elsa output. It also passes the cancellation token.

There is one generated activity per task. There is no shared `RunTask` activity in this option. Wrappers still exist, but developers do not write them by hand.

## How parameters move

The generator creates one typed Elsa input for each top-level field of the task's input record. Nested objects remain nested. The demo has one typed output per task, which can contain many fields.

| Task | Elsa inputs | Elsa output |
|---|---|---|
| Create invoice | Customer, Lines, Tax | Invoice |
| Plan shipment | Invoice, Origin, Packages, Rules | Shipment |

The flow author chooses where each input comes from. The generator cannot guess that choice.

- Pass a whole object when the earlier step provides the expected type.
- Use an input expression to build an object from different sources.
- Use a fixed value for a setting that does not change.

These mappings sit on the task activity's inputs. They do not need a separate mapping activity. Similar field names do not make different types compatible; those need an explicit mapping.

## Complex objects in the demo

**Invoice:** A customer contains an address. Order lines are a list. Tax rules contain a rate and currency. Earlier Elsa steps provide these values. The task returns the customer, lines, and nested totals: ₹3,500 subtotal + ₹630 tax = **₹4,130**.

**Shipment:** The task receives that invoice, a warehouse with an address, and packages with dimensions and SKU lists. An expression on its Rules input reads the invoice total to decide whether a signature is needed. Result: **two packages, 3 kg, signature required**.

An invalid quantity stops the Elsa flow and records the field path `Lines[1].Quantity`. An unknown package SKU is also rejected.

## What stays separate

| Part | Responsibility | Knows about Elsa? |
|---|---|---|
| Task project | Contracts, data types, business rules | No |
| Generator | Reads task types and writes adapter code | Yes, through its generated code |
| Elsa project | Adapters, flow definitions, input mappings, task registration | Yes |

If we switch engines, task code and its contracts can stay. The adapter generator, engine integration, and flows must change. Moving workflows already in progress is a separate problem.

## Benefits and costs

**Benefits:** Plain C# business code, typed inputs and outputs, no manual wrappers, and tasks that can be tested without Elsa.

**Costs:** We maintain a generator. New or changed tasks need generation, compilation, and deployment. Contract changes need version planning so existing flows do not break.

## What is proven, and what remains

The demo runs on Elsa 3.8.0 with .NET 9. Seven checks passed, covering results, nested handover, bad input, direct task execution, and cancellation. See the [captured results](../demo-output.txt).

This does **not** yet prove the consultant experience in Elsa Studio. The demo uses C# flow definitions and input expressions. Typed activity properties alone do not create a friendly nested-field editor.

Before production, we still need:

- Studio editing and saved-expression support for nested inputs and lists.
- Save/load and restart/resume tests for custom data types.
- Checks for missing mappings and malformed nested values.
- Stable task and activity versions, plus clear generator errors.
- Generation wired into the build and CI. Today it runs through `run-demo.sh`.
- Retry handling that avoids duplicate writes, before adding real external services.

The generator currently supports the demo's record constructor pattern. It is a small command-line generator, not a full Roslyn source generator. Source values and invoice IDs are sample data.

## Try it

Run `./run-demo.sh` from the project folder. It generates adapters, runs both scenarios, and checks the results.

See the [demo walkthrough](../README.md) for file locations and the exact mappings.

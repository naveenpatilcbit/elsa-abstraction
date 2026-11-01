using Demo.Tasks;
using Demo.Validation.Generated;
using Demo.Validation.Tasks;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Demo.Validation;

public static class TestHost
{
    public static ServiceProvider Create(bool includeV2 = true)
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa =>
        {
            elsa.AddActivitiesFrom<Demo.Elsa.Generated.CreateInvoiceActivity>();
            elsa.AddActivity<ProbeActivity>();
            elsa.AddActivity<QuoteV1Activity>();
            if (includeV2) elsa.AddActivity<QuoteV2Activity>();
            elsa.AddActivity<ScopeActivity>();
            elsa.AddActivity<PaymentActivity>();
            elsa.AddActivity<WaitForApproval>();
            elsa.AddActivity<CheckShipment>();
        });
        services.AddSingleton<FakeLedger>();
        services.AddScoped<ScopeStamp>();
        services.AddScoped<RunObservations>();
        services.AddScoped<IAppTask<InvoiceInput, Invoice>, CountingInvoiceTask>();
        services.AddScoped<IAppTask<ShipmentInput, Shipment>, PlanShipmentTask>();
        services.AddScoped<IAppTask<ProbeInput, ProbeInput>, ProbeTask>();
        services.AddScoped<IAppTask<QuoteV1Input, Quote>, QuoteV1Task>();
        services.AddScoped<IAppTask<QuoteV2Input, Quote>, QuoteV2Task>();
        services.AddScoped<IAppTask<ScopeInput, ScopeOutput>, ScopeTask>();
        services.AddScoped<IAppTask<PaymentInput, PaymentOutput>, PaymentTask>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}

public sealed class RunObservations
{
    public int InvoiceCalls { get; set; }
    public Shipment? Shipment { get; set; }
}
public sealed class CountingInvoiceTask(RunObservations observations) : IAppTask<InvoiceInput, Invoice>
{
    public Task<Invoice> ExecuteAsync(InvoiceInput input, CancellationToken cancellationToken)
    {
        observations.InvoiceCalls++;
        return new CreateInvoiceTask().ExecuteAsync(input, cancellationToken);
    }
}

// Test-only pause step. Waiting is Elsa's job, not business task code.
public sealed class WaitForApproval : Activity
{
    protected override void Execute(ActivityExecutionContext context) =>
        context.CreateBookmark(new CreateBookmarkArgs { BookmarkName = "validation-approval" });
}

// Test observer with serializable inputs; avoids saving a captured C# lambda.
public sealed class CheckShipment : CodeActivity
{
    public Input<Shipment> Shipment { get; set; } = default!;
    protected override void Execute(ActivityExecutionContext context)
    {
        var shipment = context.Get(Shipment)!;
        if (shipment.TotalWeightKg != 3m || shipment.Destination.PostalCode != "560001" || shipment.Packages[0].Size.Length != 40m)
            throw new InvalidOperationException("Resumed shipment lost nested data.");
        context.GetRequiredService<RunObservations>().Shipment = shipment;
    }
}

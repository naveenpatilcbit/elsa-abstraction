namespace Demo.Tasks;

public interface IAppTask<TInput, TOutput>
{
    Task<TOutput> ExecuteAsync(TInput input, CancellationToken cancellationToken);
}
public record Address(string City, string PostalCode);
public record Customer(string Id, string Email, Address BillingAddress);
public record Line(string Sku, int Quantity, decimal UnitPrice);
public record TaxRules(decimal Rate, string Currency);
public record InvoiceInput(Customer Customer, List<Line> Lines, TaxRules Tax);
public record Totals(decimal Subtotal, decimal Tax, decimal GrandTotal, string Currency);
public record Invoice(string Id, Customer Customer, List<Line> Lines, Totals Totals);
public record Warehouse(string Code, Address Address);
public record Dimensions(decimal Length, decimal Width, decimal Height);
public record Package(string Label, decimal WeightKg, Dimensions Size, List<string> Skus);
public record DeliveryRules(decimal MaxWeightKg, bool SignatureRequired);
public record ShipmentInput(Invoice Invoice, Warehouse Origin, List<Package> Packages, DeliveryRules Rules);
public record Shipment(string InvoiceId, Address Destination, string OriginCode, decimal TotalWeightKg,
    bool SignatureRequired, List<Package> Packages);

public sealed class CreateInvoiceTask : IAppTask<InvoiceInput, Invoice>
{
    public Task<Invoice> ExecuteAsync(InvoiceInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input.Customer);
        if (string.IsNullOrWhiteSpace(input.Customer.BillingAddress?.PostalCode))
            throw new ArgumentException("Customer.BillingAddress.PostalCode is required.");
        if (input.Lines is not { Count: > 0 }) throw new ArgumentException("Lines must not be empty.");
        for (var i = 0; i < input.Lines.Count; i++)
        {
            if (input.Lines[i].Quantity <= 0) throw new ArgumentException($"Lines[{i}].Quantity must be greater than zero.");
            if (input.Lines[i].UnitPrice < 0) throw new ArgumentException($"Lines[{i}].UnitPrice must not be negative.");
        }
        if (input.Tax.Rate is < 0 or > 1) throw new ArgumentException("Tax.Rate must be between zero and one.");
        var subtotal = input.Lines.Sum(x => x.Quantity * x.UnitPrice);
        var tax = decimal.Round(subtotal * input.Tax.Rate, 2, MidpointRounding.AwayFromZero);
        return Task.FromResult(new Invoice("INV-DEMO-001", input.Customer, input.Lines,
            new Totals(subtotal, tax, subtotal + tax, input.Tax.Currency)));
    }
}

public sealed class PlanShipmentTask : IAppTask<ShipmentInput, Shipment>
{
    public Task<Shipment> ExecuteAsync(ShipmentInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Packages is not { Count: > 0 }) throw new ArgumentException("Packages must not be empty.");
        var allowed = input.Invoice.Lines.Select(x => x.Sku).ToHashSet();
        for (var i = 0; i < input.Packages.Count; i++)
        {
            var p = input.Packages[i];
            if (p.WeightKg <= 0 || p.WeightKg > input.Rules.MaxWeightKg)
                throw new ArgumentException($"Packages[{i}].WeightKg must be positive and within the delivery limit.");
            if (p.Size.Length <= 0 || p.Size.Width <= 0 || p.Size.Height <= 0)
                throw new ArgumentException($"Packages[{i}].Size dimensions must be positive.");
            if (p.Skus.Count == 0 || p.Skus.Any(s => !allowed.Contains(s)))
                throw new ArgumentException($"Packages[{i}].Skus must refer to invoice items.");
        }
        return Task.FromResult(new Shipment(input.Invoice.Id, input.Invoice.Customer.BillingAddress,
            input.Origin.Code, input.Packages.Sum(x => x.WeightKg), input.Rules.SignatureRequired, input.Packages));
    }
}

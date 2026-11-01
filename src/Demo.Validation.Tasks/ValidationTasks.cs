using Demo.Tasks;

namespace Demo.Validation.Tasks;

// Shape checks: the adapter must preserve each of these types and their values.
public enum ShippingSpeed { Standard, Express }
public record ProbeInput(string Reference, int Count, decimal Amount, bool Enabled,
    DateTimeOffset When, ShippingSpeed Speed, int? OptionalCount, string? Note,
    Address Address, List<Line> Lines, Dictionary<string, decimal> Rates, string[] Tags);
public sealed class ProbeTask : IAppTask<ProbeInput, ProbeInput>
{
    public Task<ProbeInput> ExecuteAsync(ProbeInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(input);
    }
}

// Same persisted activity name, but separate contracts and implementations.
public record QuoteV1Input(decimal Amount);
public record QuoteV2Input(decimal Amount, decimal Discount);
public record Quote(decimal Total, string AppliedVersion);
[TaskIdentity("Validation.Quote", 1)]
public sealed class QuoteV1Task : IAppTask<QuoteV1Input, Quote>
{
    public Task<Quote> ExecuteAsync(QuoteV1Input input, CancellationToken cancellationToken) =>
        Task.FromResult(new Quote(input.Amount, "v1"));
}
[TaskIdentity("Validation.Quote", 2)]
public sealed class QuoteV2Task : IAppTask<QuoteV2Input, Quote>
{
    public Task<Quote> ExecuteAsync(QuoteV2Input input, CancellationToken cancellationToken) =>
        Task.FromResult(new Quote(input.Amount - input.Discount, "v2"));
}

// A small scoped dependency makes lifetime and concurrent-run mistakes visible.
public sealed class ScopeStamp { public Guid Id { get; } = Guid.NewGuid(); }
public record ScopeInput(string RequestId);
public record ScopeOutput(string RequestId, Guid ScopeId);
public sealed class ScopeTask(ScopeStamp stamp) : IAppTask<ScopeInput, ScopeOutput>
{
    public async Task<ScopeOutput> ExecuteAsync(ScopeInput input, CancellationToken cancellationToken)
    {
        await Task.Delay(5, cancellationToken);
        return new(input.RequestId, stamp.Id);
    }
}

// Simulates a completed write followed by a lost response. This is task-owned retry safety.
public sealed class FakeLedger
{
    private readonly Dictionary<string, string> receipts = new();
    public int Writes { get; private set; }
    public string Save(string key)
    {
        lock (receipts)
        {
            if (receipts.TryGetValue(key, out var receipt)) return receipt;
            receipt = "receipt-" + key;
            receipts.Add(key, receipt);
            Writes++;
            throw new IOException("Simulated response loss after the write.");
        }
    }
}
public record PaymentInput(string RequestId);
public record PaymentOutput(string Receipt);
public sealed class PaymentTask(FakeLedger ledger) : IAppTask<PaymentInput, PaymentOutput>
{
    public Task<PaymentOutput> ExecuteAsync(PaymentInput input, CancellationToken cancellationToken) =>
        Task.FromResult(new PaymentOutput(ledger.Save(input.RequestId)));
}

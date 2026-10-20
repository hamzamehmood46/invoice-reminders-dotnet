using Nudge.Engine;

namespace Nudge.Engine.Tests;

public class PaymentTests
{
    private readonly Shop _s = new();

    [Fact]
    public void A_partial_payment_reduces_the_balance_and_marks_the_invoice_partially_paid()
    {
        var id = _s.Sent(100_000);

        var result = _s.Pay(id, 30_000);

        Assert.True(result.Ok);
        Assert.Equal(InvoiceStatus.PartiallyPaid, _s.Get(id).Status);
        Assert.Equal(70_000, _s.Get(id).BalanceCents);
        Assert.Equal(30_000, _s.Get(id).PaidCents);
    }

    [Fact]
    public void Paying_the_whole_balance_marks_the_invoice_paid()
    {
        var id = _s.Sent(100_000, taxBp: 1000);

        var result = _s.Pay(id, 110_000);

        Assert.Equal("Paid in full.", result.Message);
        Assert.Equal(InvoiceStatus.Paid, _s.Get(id).Status);
        Assert.Equal(0, _s.Get(id).BalanceCents);
    }

    [Fact]
    public void Several_payments_add_up_to_paid()
    {
        var id = _s.Sent(100_000);

        _s.Pay(id, 25_000, "a");
        _s.Pay(id, 25_000, "b");
        _s.Pay(id, 50_000, "c");

        Assert.Equal(InvoiceStatus.Paid, _s.Get(id).Status);
        Assert.Equal(3, _s.Get(id).Payments.Count);
    }

    [Fact]
    public void An_overpayment_is_refused_and_records_nothing()
    {
        var id = _s.Sent(100_000);

        var result = _s.Pay(id, 100_001);

        Assert.False(result.Ok);
        Assert.Contains("more than the balance", result.Message);
        Assert.Empty(_s.Get(id).Payments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void A_payment_must_be_more_than_zero(long cents)
    {
        var id = _s.Sent();

        Assert.False(_s.Pay(id, cents).Ok);
    }

    [Fact]
    public void Payments_are_refused_for_drafts_voided_paid_and_unknown_invoices()
    {
        var draft = _s.Draft();
        var voided = _s.Sent();
        _s.Billing.Void(voided);
        var paid = _s.Sent();
        _s.Pay(paid, 100_000, "full");

        Assert.False(_s.Pay(draft, 100, "d").Ok);
        Assert.False(_s.Pay(voided, 100, "v").Ok);
        Assert.False(_s.Pay(paid, 100, "extra").Ok);
        Assert.False(_s.Pay(Guid.NewGuid(), 100, "ghost").Ok);
    }

    [Fact]
    public void A_payment_needs_an_idempotency_key()
    {
        var id = _s.Sent();

        Assert.False(_s.Billing.RecordPayment(id, 100, "card", " ").Ok);
    }

    [Fact]
    public void Retrying_a_payment_with_the_same_key_returns_the_original_and_never_counts_it_twice()
    {
        var id = _s.Sent(100_000);
        var first = _s.Pay(id, 40_000, "stripe-evt-1");

        var again = _s.Pay(id, 40_000, "stripe-evt-1");

        Assert.True(again.Replayed);
        Assert.Equal(first.Payment!.Id, again.Payment!.Id);
        Assert.Equal(60_000, _s.Get(id).BalanceCents);
        Assert.Single(_s.Get(id).Payments);
    }

    [Fact]
    public void A_replay_is_recognized_even_after_the_invoice_is_paid_off()
    {
        var id = _s.Sent(100_000);
        _s.Pay(id, 100_000, "evt");

        var replay = _s.Pay(id, 100_000, "evt");

        Assert.True(replay.Ok);
        Assert.True(replay.Replayed);
        Assert.Single(_s.Get(id).Payments);
    }

    [Fact]
    public void The_same_key_on_a_different_invoice_is_a_different_payment()
    {
        var a = _s.Sent(10_000);
        var b = _s.Sent(10_000);

        _s.Pay(a, 10_000, "same");
        var other = _s.Pay(b, 10_000, "same");

        Assert.False(other.Replayed);
        Assert.Equal(InvoiceStatus.Paid, _s.Get(b).Status);
    }

    [Fact]
    public void Different_keys_are_different_payments()
    {
        var id = _s.Sent(100_000);

        _s.Pay(id, 10_000, "a");
        _s.Pay(id, 10_000, "b");

        Assert.Equal(80_000, _s.Get(id).BalanceCents);
    }

    [Fact]
    public async Task Fifty_parallel_deliveries_of_the_same_payment_record_exactly_one()
    {
        var id = _s.Sent(100_000);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => _s.Pay(id, 100_000, "webhook-1"))));

        Assert.Equal(1, results.Count(r => !r.Replayed));
        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Single(_s.Get(id).Payments);
        Assert.Equal(InvoiceStatus.Paid, _s.Get(id).Status);
    }

    [Fact]
    public async Task Two_hundred_parallel_one_cent_payments_on_a_one_dollar_invoice_never_overpay()
    {
        var id = _s.Sent(100);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => _s.Pay(id, 1, "p" + i))));

        var invoice = _s.Get(id);
        Assert.Equal(100, results.Count(r => r.Ok));
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(100, invoice.PaidCents);
        Assert.Equal(0, invoice.BalanceCents);
    }
}

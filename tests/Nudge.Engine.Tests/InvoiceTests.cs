using Nudge.Engine;

namespace Nudge.Engine.Tests;

public class InvoiceTests
{
    private readonly Shop _s = new();

    [Theory]
    [InlineData(10_000, 825, 825)]
    [InlineData(999, 825, 82)]
    [InlineData(1_000, 825, 83)]
    [InlineData(1_000, 0, 0)]
    [InlineData(333, 1000, 33)]
    [InlineData(335, 1000, 34)]
    [InlineData(100_000, 2000, 20_000)]
    [InlineData(1, 5000, 1)]
    [InlineData(0, 825, 0)]
    public void Tax_is_rounded_half_up_to_the_cent(long subtotal, int basisPoints, long expectedTax)
    {
        var invoice = new Invoice(Guid.NewGuid(), "X", Guid.NewGuid(), [new InvoiceLine("x", 1, subtotal)], basisPoints, null, null, InvoiceStatus.Sent, 0, false, []);

        Assert.Equal(expectedTax, invoice.TaxCents);
        Assert.Equal(subtotal + expectedTax, invoice.TotalCents);
    }

    [Fact]
    public void Lines_multiply_quantity_by_price_and_add_up()
    {
        var invoice = new Invoice(Guid.NewGuid(), "X", Guid.NewGuid(), [new("Design", 3, 12_500), new("Hosting", 12, 999)], 0, null, null, InvoiceStatus.Sent, 0, false, []);

        Assert.Equal(37_500 + 11_988, invoice.SubtotalCents);
    }

    [Fact]
    public void Money_is_formatted_with_thousands_separators_and_two_decimals()
    {
        Assert.Equal("$1,234.50", Billing.Money(123_450));
        Assert.Equal("$0.05", Billing.Money(5));
        Assert.Equal("$0.00", Billing.Money(0));
    }

    [Fact]
    public void Invoices_are_numbered_in_sequence()
    {
        _s.Draft();
        _s.Draft();
        _s.Draft();

        Assert.Equal(["INV-0001", "INV-0002", "INV-0003"], _s.Billing.Invoices().Select(i => i.Number));
    }

    [Fact]
    public void A_draft_has_no_dates_and_nothing_owed()
    {
        var invoice = _s.Get(_s.Draft());

        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
        Assert.Null(invoice.IssuedOn);
        Assert.Null(invoice.DueOn);
        Assert.Equal(0, invoice.BalanceCents);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(60)]
    public void Sending_sets_the_issue_date_to_today_and_the_due_date_by_the_clients_terms(int terms)
    {
        var shop = new Shop(terms: terms);
        var id = shop.Sent();

        var invoice = shop.Get(id);

        Assert.Equal(new DateOnly(2026, 10, 12), invoice.IssuedOn);
        Assert.Equal(new DateOnly(2026, 10, 12).AddDays(terms), invoice.DueOn);
        Assert.Equal(InvoiceStatus.Sent, invoice.Status);
        Assert.Equal(100_000, invoice.BalanceCents);
    }

    [Fact]
    public void Only_a_draft_can_be_sent_and_only_once()
    {
        var id = _s.Sent();

        Assert.False(_s.Billing.Send(id).Ok);
        Assert.False(_s.Billing.Send(Guid.NewGuid()).Ok);
    }

    [Fact]
    public void An_invoice_for_nothing_cannot_be_sent()
    {
        var created = _s.Billing.CreateInvoice(_s.Client, [new InvoiceLine("Free sample", 1, 0)]);
        var id = _s.Billing.Invoices().Single().Id;

        Assert.True(created.Ok);
        Assert.False(_s.Billing.Send(id).Ok);
    }

    [Fact]
    public void Bad_invoices_are_refused_and_nothing_is_created()
    {
        Assert.False(_s.Billing.CreateInvoice(Guid.NewGuid(), [new("x", 1, 100)]).Ok);
        Assert.False(_s.Billing.CreateInvoice(_s.Client, []).Ok);
        Assert.False(_s.Billing.CreateInvoice(_s.Client, [new("x", 0, 100)]).Ok);
        Assert.False(_s.Billing.CreateInvoice(_s.Client, [new("x", 1, -1)]).Ok);
        Assert.False(_s.Billing.CreateInvoice(_s.Client, [new(" ", 1, 100)]).Ok);
        Assert.False(_s.Billing.CreateInvoice(_s.Client, [new("x", 1, 100)], 3001).Ok);
        Assert.False(_s.Billing.CreateInvoice(_s.Client, [new("x", 1, 100)], -1).Ok);
        Assert.Empty(_s.Billing.Invoices());
    }

    [Fact]
    public void An_unpaid_invoice_can_be_voided_but_one_with_payments_cannot()
    {
        var unpaid = _s.Sent();
        var paid = _s.Sent();
        _s.Pay(paid, 100, "p");

        Assert.True(_s.Billing.Void(unpaid).Ok);
        Assert.False(_s.Billing.Void(unpaid).Ok);
        Assert.False(_s.Billing.Void(paid).Ok);
        Assert.Equal(0, _s.Get(unpaid).BalanceCents);
    }

    [Fact]
    public void An_invoice_is_overdue_only_once_it_is_open_and_past_its_due_date()
    {
        var id = _s.Sent();

        Assert.False(_s.Get(id).IsOverdue(_s.Billing.Today));
        _s.Days(30);
        Assert.False(_s.Get(id).IsOverdue(_s.Billing.Today));
        _s.Days(1);
        Assert.True(_s.Get(id).IsOverdue(_s.Billing.Today));
        _s.Pay(id, 100_000);
        Assert.False(_s.Get(id).IsOverdue(_s.Billing.Today));
    }

    [Fact]
    public void Every_step_is_written_to_the_invoices_history()
    {
        var id = _s.Sent();
        _s.Pay(id, 40_000, "a");
        _s.Pay(id, 60_000, "b");

        var history = _s.Billing.History(id).Select(h => h.Text).ToList();

        Assert.Contains(history, t => t.StartsWith("Draft INV-0001"));
        Assert.Contains(history, t => t.StartsWith("Sent. Due"));
        Assert.Contains(history, t => t.Contains("$400.00") && t.Contains("$600.00 left"));
        Assert.Contains(history, t => t.Contains("Paid in full"));
    }

    [Fact]
    public void Client_terms_are_kept_within_a_sensible_range()
    {
        var id = _s.Billing.AddClient("Long terms", "x@y.com", 9999);

        Assert.Equal(180, _s.Billing.Clients().Single(c => c.Id == id).TermsDays);
    }
}

using Nudge.Engine;

namespace Nudge.Engine.Tests;

/// <summary>A business with one client, on a Monday morning, ready to invoice.</summary>
public sealed class Shop
{
    public SimulatedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
    public Billing Billing { get; }
    public Guid Client { get; }
    public FakeEmailSender Mail { get; } = new();

    public Shop(ReminderPolicy? policy = null, int terms = 30)
    {
        Billing = new Billing(Clock, policy);
        Client = Billing.AddClient("Acme", "ap@acme.com", terms);
    }

    public Guid Sent(long cents = 100_000, int taxBp = 0, Guid? client = null, string description = "Consulting")
    {
        var created = Billing.CreateInvoice(client ?? Client, [new InvoiceLine(description, 1, cents)], taxBp);
        var id = Billing.Invoices().Single(i => i.Number == created.Message).Id;
        Assert.True(Billing.Send(id).Ok);
        return id;
    }

    public Guid Draft(long cents = 100_000, int taxBp = 0)
    {
        var created = Billing.CreateInvoice(Client, [new InvoiceLine("Consulting", 1, cents)], taxBp);
        return Billing.Invoices().Single(i => i.Number == created.Message).Id;
    }

    public Invoice Get(Guid id) => Billing.Invoice(id)!;

    public void Days(int days) => Clock.Advance(TimeSpan.FromDays(days));

    public PaymentOutcome Pay(Guid id, long cents, string key = "k1", string method = "card") => Billing.RecordPayment(id, cents, method, key);

    public IReadOnlyList<Reminder> Reminders(Guid id) => Billing.Reminders().Where(r => r.InvoiceId == id).ToList();
}

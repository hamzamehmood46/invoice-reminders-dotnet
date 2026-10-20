using Nudge.Engine;

namespace Nudge.Web;

/// <summary>A small business, its books and its mail server, on a clock you control.</summary>
public sealed class AppState
{
    public static readonly DateTimeOffset Start = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    public SimulatedClock Clock { get; private set; } = null!;
    public Billing Billing { get; private set; } = null!;
    public FakeEmailSender Mail { get; private set; } = null!;
    public bool AutoRun { get; set; } = true;

    public string Message { get; private set; } = "";
    public bool MessageOk { get; private set; } = true;

    // Remembers the last payment of each invoice so a "retry" can resend the same event.
    private readonly Dictionary<Guid, (long Cents, string Key)> _lastPayment = [];
    private int _keys;

    public AppState() => Reset();

    public DateTimeOffset Now => Clock.GetUtcNow();

    public void Reset()
    {
        Clock = new SimulatedClock(Start);
        Billing = new Billing(Clock);
        Mail = new FakeEmailSender();
        _lastPayment.Clear();
        Seed();
        Message = "";
    }

    private Guid Client(string name, string email, int terms) => Billing.AddClient(name, email, terms);

    private Guid Invoice(Guid client, string description, long cents, int taxBp = 0)
    {
        var created = Billing.CreateInvoice(client, [new InvoiceLine(description, 1, cents)], taxBp);
        var id = Billing.Invoices().Single(i => i.Number == created.Message).Id;
        Billing.Send(id);
        return id;
    }

    private void Seed()
    {
        var acme = Client("Acme Manufacturing", "ap@acme.com", 30);
        var beta = Client("Beta Studio", "billing@beta.studio", 15);
        var cedar = Client("Cedar & Co", "accounts@cedar.co", 45);
        var delta = Client("Delta Labs", "pay@deltalabs.io", 30);

        var acmeInvoice = Invoice(acme, "Website rebuild", 480_000, 825);
        for (var i = 0; i < 7; i++)
        {
            DailyJob();
            Clock.Advance(TimeSpan.FromDays(1));
        }

        var betaInvoice = Invoice(beta, "Brand identity", 125_000);
        for (var i = 0; i < 7; i++)
        {
            DailyJob();
            Clock.Advance(TimeSpan.FromDays(1));
        }

        Invoice(cedar, "Quarterly retainer", 920_000);
        for (var i = 0; i < 20; i++)
        {
            DailyJob();
            Clock.Advance(TimeSpan.FromDays(1));
        }

        Billing.RecordPayment(betaInvoice, 50_000, "card", "seed-beta-1");
        _lastPayment[betaInvoice] = (50_000, "seed-beta-1");
        Invoice(delta, "Data pipeline audit", 260_000);
        for (var i = 0; i < 20; i++)
        {
            DailyJob();
            Clock.Advance(TimeSpan.FromDays(1));
        }

        Invoice(acme, "Support plan", 90_000);
        _ = acmeInvoice;
        DailyJob();
    }

    /// <summary>What a nightly job would do: charge late fees, queue the day's reminders and send the emails.</summary>
    public void DailyJob()
    {
        Billing.ApplyLateFees();
        Billing.RunReminders();
        Billing.SendEmails(Mail);
    }

    public void Wait(TimeSpan by)
    {
        var days = (int)Math.Round(by.TotalDays);
        for (var d = 0; d < days; d++)
        {
            Clock.Advance(TimeSpan.FromDays(1));
            if (AutoRun)
            {
                DailyJob();
            }
        }

        Message = AutoRun ? $"Moved forward {days} day(s), running the daily job each night." : $"Moved forward {days} day(s). The daily job did not run.";
        MessageOk = true;
    }

    public void Say(string message, bool ok = true)
    {
        Message = message;
        MessageOk = ok;
    }

    public void Pay(Guid invoiceId, long cents)
    {
        var key = "evt-" + ++_keys;
        var result = Billing.RecordPayment(invoiceId, cents, "card", key);
        if (result.Ok)
        {
            _lastPayment[invoiceId] = (cents, key);
        }

        Say(result.Message, result.Ok);
    }

    public bool CanRetry(Guid id) => _lastPayment.ContainsKey(id);

    public void RetryLastPayment(Guid invoiceId)
    {
        if (!_lastPayment.TryGetValue(invoiceId, out var last))
        {
            Say("There is no payment to retry on this invoice yet.", false);
            return;
        }

        var result = Billing.RecordPayment(invoiceId, last.Cents, "card", last.Key);
        Say(result.Replayed ? "The payment provider sent the same event again. It was recognized and counted once." : result.Message, result.Ok);
    }
}


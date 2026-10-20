using System.Globalization;

namespace Nudge.Engine;

public interface IEmailSender
{
    /// <summary>Sends one email. Returns null on success, or the error message.</summary>
    string? Send(string to, string subject, string body);
}

public enum MailMode { Up, Down }

public sealed class FakeEmailSender : IEmailSender
{
    private readonly object _gate = new();
    private readonly List<(string To, string Subject, string Body)> _sent = [];

    public MailMode Mode { get; set; } = MailMode.Up;

    public IReadOnlyList<(string To, string Subject, string Body)> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToList();
            }
        }
    }

    public string? Send(string to, string subject, string body)
    {
        if (Mode == MailMode.Down)
        {
            return "The mail server is not answering.";
        }

        lock (_gate)
        {
            _sent.Add((to, subject, body));
        }

        return null;
    }
}

/// <summary>
/// Invoices, payments and reminders for a small business. Money is whole cents, time comes from an injected clock,
/// and every action that could be repeated (a payment retried, a reminder run twice) is safe to repeat.
/// </summary>
public sealed class Billing
{
    public const int MaxEmailAttempts = 3;
    private static readonly TimeSpan[] EmailRetryDelays = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Client> _clients = [];
    private readonly Dictionary<Guid, Invoice> _invoices = [];
    private readonly Dictionary<Guid, List<HistoryEntry>> _history = [];
    private readonly Dictionary<(Guid, string), Payment> _paymentsByKey = [];
    private readonly HashSet<(Guid Invoice, int Step)> _stepsDone = [];
    private readonly List<Reminder> _reminders = [];
    private int _nextNumber = 1;

    public Billing(TimeProvider time, ReminderPolicy? policy = null)
    {
        _time = time;
        Policy = policy ?? ReminderPolicy.Default;
    }

    public ReminderPolicy Policy { get; }

    private DateTimeOffset Now => _time.GetUtcNow();
    public DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    // ---- Reading ----

    private T Locked<T>(Func<T> read)
    {
        lock (_gate)
        {
            return read();
        }
    }

    public IReadOnlyList<Client> Clients() => Locked(() => _clients.Values.OrderBy(c => c.Name).ToList());
    public IReadOnlyList<Invoice> Invoices() => Locked(() => _invoices.Values.OrderBy(i => i.Number).ToList());
    public IReadOnlyList<Reminder> Reminders() => Locked(() => _reminders.OrderBy(r => r.CreatedAt).ToList());
    public Invoice? Invoice(Guid id) => Locked(() => _invoices.GetValueOrDefault(id));
    public IReadOnlyList<HistoryEntry> History(Guid invoiceId) => Locked(() => _history.TryGetValue(invoiceId, out var h) ? h.ToList() : []);

    // ---- Clients ----

    public Guid AddClient(string name, string email, int termsDays = 30)
    {
        lock (_gate)
        {
            var client = new Client(Guid.NewGuid(), name.Trim(), email.Trim(), Math.Clamp(termsDays, 0, 180), false);
            _clients[client.Id] = client;
            return client.Id;
        }
    }

    public void PauseReminders(Guid clientId, bool paused)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(clientId, out var c))
            {
                _clients[clientId] = c with { RemindersPaused = paused };
            }
        }
    }

    // ---- Invoices ----

    public Outcome CreateInvoice(Guid clientId, IReadOnlyList<InvoiceLine> lines, int taxBasisPoints = 0)
    {
        lock (_gate)
        {
            if (!_clients.ContainsKey(clientId))
            {
                return new Outcome(false, "No such client.");
            }

            if (lines.Count == 0)
            {
                return new Outcome(false, "An invoice needs at least one line.");
            }

            if (lines.Any(l => l.Quantity < 1 || l.UnitCents < 0 || string.IsNullOrWhiteSpace(l.Description)))
            {
                return new Outcome(false, "Each line needs a description, a quantity of at least 1 and a price that is not negative.");
            }

            if (taxBasisPoints is < 0 or > 3000)
            {
                return new Outcome(false, "Tax must be between 0% and 30%.");
            }

            var invoice = new Invoice(Guid.NewGuid(), $"INV-{_nextNumber++:0000}", clientId, lines.ToList(), taxBasisPoints, null, null, InvoiceStatus.Draft, 0, false, []);
            _invoices[invoice.Id] = invoice;
            Log(invoice.Id, $"Draft {invoice.Number} created.");
            return new Outcome(true, invoice.Number);
        }
    }

    /// <summary>Issues a draft: it gets an issue date today and is due after the client's payment terms.</summary>
    public Outcome Send(Guid invoiceId)
    {
        lock (_gate)
        {
            if (!_invoices.TryGetValue(invoiceId, out var invoice))
            {
                return new Outcome(false, "No such invoice.");
            }

            if (invoice.Status != InvoiceStatus.Draft)
            {
                return new Outcome(false, $"Only a draft can be sent; this invoice is {invoice.Status}.");
            }

            if (invoice.TotalCents <= 0)
            {
                return new Outcome(false, "An invoice for nothing cannot be sent.");
            }

            var terms = _clients[invoice.ClientId].TermsDays;
            var updated = invoice with { Status = InvoiceStatus.Sent, IssuedOn = Today, DueOn = Today.AddDays(terms) };
            _invoices[invoiceId] = updated;
            Log(invoiceId, $"Sent. Due {updated.DueOn:yyyy-MM-dd}.");
            return new Outcome(true, $"Sent. Due {updated.DueOn:yyyy-MM-dd}.");
        }
    }

    public Outcome Void(Guid invoiceId)
    {
        lock (_gate)
        {
            if (!_invoices.TryGetValue(invoiceId, out var invoice))
            {
                return new Outcome(false, "No such invoice.");
            }

            if (invoice.Status == InvoiceStatus.Void)
            {
                return new Outcome(false, "Already void.");
            }

            if (invoice.Payments.Count > 0)
            {
                return new Outcome(false, "An invoice with payments cannot be voided.");
            }

            _invoices[invoiceId] = invoice with { Status = InvoiceStatus.Void };
            Log(invoiceId, "Voided.");
            return new Outcome(true, "Voided.");
        }
    }

    /// <summary>A disputed invoice is not chased and gets no late fee until the dispute is cleared.</summary>
    public Outcome SetDisputed(Guid invoiceId, bool disputed)
    {
        lock (_gate)
        {
            if (!_invoices.TryGetValue(invoiceId, out var invoice))
            {
                return new Outcome(false, "No such invoice.");
            }

            _invoices[invoiceId] = invoice with { Disputed = disputed };
            Log(invoiceId, disputed ? "Marked disputed: reminders and late fees paused." : "Dispute cleared.");
            return new Outcome(true, disputed ? "Marked disputed." : "Dispute cleared.");
        }
    }

    // ---- Payments ----

    /// <summary>
    /// Records a payment. The idempotency key makes a retry (a double click, a repeated webhook from the payment
    /// provider) return the original payment instead of counting the money twice. An overpayment is refused.
    /// </summary>
    public PaymentOutcome RecordPayment(Guid invoiceId, long amountCents, string method, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new PaymentOutcome(false, "A payment needs an idempotency key.", null);
        }

        lock (_gate)
        {
            if (_paymentsByKey.TryGetValue((invoiceId, idempotencyKey), out var existing))
            {
                return new PaymentOutcome(true, "Already recorded.", existing, Replayed: true);
            }

            if (!_invoices.TryGetValue(invoiceId, out var invoice))
            {
                return new PaymentOutcome(false, "No such invoice.", null);
            }

            if (!invoice.IsOpen)
            {
                return new PaymentOutcome(false, $"A {invoice.Status} invoice cannot take a payment.", null);
            }

            if (amountCents <= 0)
            {
                return new PaymentOutcome(false, "A payment must be more than zero.", null);
            }

            if (amountCents > invoice.BalanceCents)
            {
                return new PaymentOutcome(false, $"That is more than the balance of {Money(invoice.BalanceCents)}.", null);
            }

            var payment = new Payment(Guid.NewGuid(), invoiceId, amountCents, method, Now, idempotencyKey);
            var paid = invoice.PaidCents + amountCents;
            var updated = invoice with
            {
                Payments = invoice.Payments.Append(payment).ToList(),
                Status = paid >= invoice.TotalCents ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid,
            };
            _invoices[invoiceId] = updated;
            _paymentsByKey[(invoiceId, idempotencyKey)] = payment;
            Log(invoiceId, $"Payment of {Money(amountCents)} by {method}. " + (updated.Status == InvoiceStatus.Paid ? "Paid in full." : $"{Money(updated.BalanceCents)} left."));
            return new PaymentOutcome(true, updated.Status == InvoiceStatus.Paid ? "Paid in full." : "Partial payment recorded.", payment);
        }
    }

    // ---- Late fees ----

    /// <summary>Adds the late fee, once, to open invoices that are far enough past due. Returns how many were charged.</summary>
    public int ApplyLateFees()
    {
        var policy = Policy.LateFee;
        if (policy.Kind == LateFeeKind.None)
        {
            return 0;
        }

        lock (_gate)
        {
            var charged = 0;
            foreach (var invoice in _invoices.Values.Where(i => i.IsOpen && !i.Disputed && i.LateFeeCents == 0 && i.DaysPastDue(Today) >= policy.AfterDays && i.BalanceCents > 0).ToList())
            {
                var fee = policy.Kind == LateFeeKind.Flat ? policy.FlatCents : (invoice.BalanceCents * policy.PercentBasisPoints + 5000) / 10_000;
                fee = policy.CapCents > 0 ? Math.Min(fee, policy.CapCents) : fee;
                if (fee <= 0)
                {
                    continue;
                }

                _invoices[invoice.Id] = invoice with { LateFeeCents = fee };
                Log(invoice.Id, $"Late fee of {Money(fee)} added ({invoice.DaysPastDue(Today)} days past due).");
                charged++;
            }

            return charged;
        }
    }

    // ---- Reminders ----

    /// <summary>
    /// Queues the reminder each open invoice is due for today. A step is only ever sent once per invoice, so running this
    /// repeatedly is safe. If several steps were missed (nobody ran it for a week), only the most recent one is sent and
    /// the earlier ones are marked skipped, so a client never receives a pile of stale emails at once.
    /// </summary>
    public int RunReminders()
    {
        lock (_gate)
        {
            var queued = 0;
            foreach (var invoice in _invoices.Values.Where(i => i.IsOpen && !i.Disputed && i.BalanceCents > 0).ToList())
            {
                var client = _clients[invoice.ClientId];
                if (client.RemindersPaused)
                {
                    continue;
                }

                var days = invoice.DaysPastDue(Today);
                var due = Enumerable.Range(0, Policy.Steps.Count).Where(i => Policy.Steps[i].DaysFromDue <= days && !_stepsDone.Contains((invoice.Id, i))).ToList();
                if (due.Count == 0)
                {
                    continue;
                }

                var latest = due[^1];
                foreach (var missed in due.Take(due.Count - 1))
                {
                    _stepsDone.Add((invoice.Id, missed));
                    _reminders.Add(NewReminder(invoice, client, missed, ReminderStatus.Skipped, "Superseded by a later reminder."));
                }

                _stepsDone.Add((invoice.Id, latest));
                _reminders.Add(NewReminder(invoice, client, latest, ReminderStatus.Queued, null));
                Log(invoice.Id, $"Reminder queued: {Policy.Steps[latest].Tone}.");
                queued++;
            }

            return queued;
        }
    }

    private Reminder NewReminder(Invoice invoice, Client client, int step, ReminderStatus status, string? note)
    {
        var tone = Policy.Steps[step].Tone;
        var (subject, body) = Compose(invoice, client, tone);
        return new Reminder(Guid.NewGuid(), invoice.Id, step, tone, Now, client.Email, subject, body, status, 0, Now, note);
    }

    private (string Subject, string Body) Compose(Invoice invoice, Client client, Tone tone)
    {
        var balance = Money(invoice.BalanceCents);
        var due = invoice.DueOn?.ToString("MMMM d", CultureInfo.InvariantCulture);
        var late = Policy.LateFee.Kind == LateFeeKind.None ? "" : $" A late fee applies after {Policy.LateFee.AfterDays} days.";
        return tone switch
        {
            Tone.Friendly => ($"Invoice {invoice.Number} is due soon", $"Hi {client.Name}, a quick heads-up that invoice {invoice.Number} for {balance} is due on {due}. Nothing to do if it is already scheduled."),
            Tone.DueToday => ($"Invoice {invoice.Number} is due today", $"Hi {client.Name}, invoice {invoice.Number} for {balance} is due today ({due}). Thank you!"),
            Tone.Gentle => ($"Invoice {invoice.Number} is past due", $"Hi {client.Name}, invoice {invoice.Number} ({balance}) was due on {due} and we have not seen the payment. If it has already gone out, please ignore this note."),
            Tone.Firm => ($"Second notice: invoice {invoice.Number}", $"Hello {client.Name}, invoice {invoice.Number} for {balance} is now {invoice.DaysPastDue(Today)} days overdue. Please arrange payment this week.{late}"),
            _ => ($"Final notice: invoice {invoice.Number}", $"Hello {client.Name}, this is a final notice for invoice {invoice.Number}: {balance} is {invoice.DaysPastDue(Today)} days overdue. Please pay today or contact us to agree a plan.{late}"),
        };
    }

    /// <summary>
    /// Sends queued reminder emails. A reminder for an invoice that has since been paid, voided or disputed is dropped.
    /// A failed send is retried after 5 and then 30 minutes, then marked failed.
    /// </summary>
    public int SendEmails(IEmailSender sender)
    {
        lock (_gate)
        {
            var sent = 0;
            for (var i = 0; i < _reminders.Count; i++)
            {
                var reminder = _reminders[i];
                if (reminder.Status != ReminderStatus.Queued || reminder.NextAttemptAt > Now)
                {
                    continue;
                }

                var invoice = _invoices[reminder.InvoiceId];
                if (!invoice.IsOpen || invoice.Disputed || invoice.BalanceCents <= 0)
                {
                    _reminders[i] = reminder with { Status = ReminderStatus.Skipped, Error = "The invoice no longer needs a reminder." };
                    continue;
                }

                var error = sender.Send(reminder.To, reminder.Subject, reminder.Body);
                var attempts = reminder.Attempts + 1;
                if (error is null)
                {
                    _reminders[i] = reminder with { Status = ReminderStatus.Sent, Attempts = attempts, Error = null };
                    Log(reminder.InvoiceId, $"Reminder emailed: {reminder.Tone}.");
                    sent++;
                }
                else if (attempts >= MaxEmailAttempts)
                {
                    _reminders[i] = reminder with { Status = ReminderStatus.Failed, Attempts = attempts, Error = error };
                    Log(reminder.InvoiceId, $"Reminder could not be emailed: {error}");
                }
                else
                {
                    _reminders[i] = reminder with { Attempts = attempts, Error = error, NextAttemptAt = Now + EmailRetryDelays[attempts - 1] };
                }
            }

            return sent;
        }
    }

    // ---- Reports ----

    /// <summary>Open balances by how late they are: not yet due, 1-30, 31-60, 61-90 and over 90 days.</summary>
    public IReadOnlyList<AgingBucket> Aging()
    {
        lock (_gate)
        {
            var labels = new[] { "Not yet due", "1-30 days", "31-60 days", "61-90 days", "Over 90 days" };
            var buckets = new (int Count, long Cents)[5];
            foreach (var invoice in _invoices.Values.Where(i => i.IsOpen && i.BalanceCents > 0))
            {
                var days = invoice.DaysPastDue(Today);
                var index = days <= 0 ? 0 : days <= 30 ? 1 : days <= 60 ? 2 : days <= 90 ? 3 : 4;
                buckets[index] = (buckets[index].Count + 1, buckets[index].Cents + invoice.BalanceCents);
            }

            return labels.Select((label, i) => new AgingBucket(label, buckets[i].Count, buckets[i].Cents)).ToList();
        }
    }

    /// <summary>What is expected in over the next weeks, by the week each open balance falls due. Anything already late is counted this week.</summary>
    public IReadOnlyList<WeekExpected> Forecast(int weeks = 8)
    {
        lock (_gate)
        {
            var monday = Today.AddDays(-(((int)Today.DayOfWeek + 6) % 7));
            var result = Enumerable.Range(0, weeks).Select(w => (Start: monday.AddDays(7 * w), Cents: 0L, Count: 0)).ToArray();
            foreach (var invoice in _invoices.Values.Where(i => i.IsOpen && i.BalanceCents > 0 && i.DueOn is not null))
            {
                var week = Math.Clamp((invoice.DueOn!.Value.DayNumber - monday.DayNumber) / 7, 0, weeks - 1);
                if (invoice.DueOn.Value.DayNumber - monday.DayNumber >= 7 * weeks)
                {
                    continue;
                }

                result[week] = (result[week].Start, result[week].Cents + invoice.BalanceCents, result[week].Count + 1);
            }

            return result.Select(r => new WeekExpected(r.Start, r.Cents, r.Count)).ToList();
        }
    }

    public IReadOnlyList<ClientSummary> ClientSummaries()
    {
        lock (_gate)
        {
            return _clients.Values.OrderBy(c => c.Name).Select(c =>
            {
                var open = _invoices.Values.Where(i => i.ClientId == c.Id && i.IsOpen && i.BalanceCents > 0).ToList();
                return new ClientSummary(c.Id, c.Name, open.Count, open.Sum(i => i.BalanceCents), open.Where(i => i.IsOverdue(Today)).Sum(i => i.BalanceCents));
            }).ToList();
        }
    }

    public static string Money(long cents) => (cents / 100m).ToString("$#,##0.00", CultureInfo.InvariantCulture);

    private void Log(Guid invoiceId, string text)
    {
        if (!_history.TryGetValue(invoiceId, out var list))
        {
            _history[invoiceId] = list = [];
        }

        list.Add(new HistoryEntry(Now, text));
    }
}

namespace Nudge.Engine;

public enum InvoiceStatus { Draft, Sent, PartiallyPaid, Paid, Void }

public enum Tone { Friendly, DueToday, Gentle, Firm, Final }

public enum ReminderStatus { Queued, Sent, Failed, Skipped }

public sealed record Client(Guid Id, string Name, string Email, int TermsDays, bool RemindersPaused);

public sealed record InvoiceLine(string Description, int Quantity, long UnitCents)
{
    public long TotalCents => Quantity * UnitCents;
}

public sealed record Payment(Guid Id, Guid InvoiceId, long AmountCents, string Method, DateTimeOffset At, string IdempotencyKey);

public sealed record HistoryEntry(DateTimeOffset At, string Text);

public sealed record Invoice(
    Guid Id,
    string Number,
    Guid ClientId,
    IReadOnlyList<InvoiceLine> Lines,
    int TaxBasisPoints,
    DateOnly? IssuedOn,
    DateOnly? DueOn,
    InvoiceStatus Status,
    long LateFeeCents,
    bool Disputed,
    IReadOnlyList<Payment> Payments)
{
    public long SubtotalCents => Lines.Sum(l => l.TotalCents);

    /// <summary>Tax rounded half up to the cent.</summary>
    public long TaxCents => (SubtotalCents * TaxBasisPoints + 5000) / 10_000;

    public long TotalCents => SubtotalCents + TaxCents + LateFeeCents;
    public long PaidCents => Payments.Sum(p => p.AmountCents);
    public long BalanceCents => Status is InvoiceStatus.Void or InvoiceStatus.Draft ? 0 : TotalCents - PaidCents;

    public bool IsOpen => Status is InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid;

    public int DaysPastDue(DateOnly today) => DueOn is { } due ? today.DayNumber - due.DayNumber : 0;

    public bool IsOverdue(DateOnly today) => IsOpen && DaysPastDue(today) > 0;
}

/// <summary>One step of the reminder schedule: how many days from the due date (negative is before it) and in what tone.</summary>
public sealed record ReminderStep(int DaysFromDue, Tone Tone);

public enum LateFeeKind { None, Flat, Percent }

public sealed record LateFeePolicy(LateFeeKind Kind, long FlatCents, int PercentBasisPoints, int AfterDays, long CapCents)
{
    public static LateFeePolicy None { get; } = new(LateFeeKind.None, 0, 0, 0, 0);
}

public sealed record ReminderPolicy(IReadOnlyList<ReminderStep> Steps, LateFeePolicy LateFee)
{
    public static ReminderPolicy Default { get; } = new(
        [new(-3, Tone.Friendly), new(0, Tone.DueToday), new(3, Tone.Gentle), new(7, Tone.Firm), new(14, Tone.Final)],
        new LateFeePolicy(LateFeeKind.Percent, 0, 150, 14, 50_000));
}

public sealed record Reminder(
    Guid Id,
    Guid InvoiceId,
    int StepIndex,
    Tone Tone,
    DateTimeOffset CreatedAt,
    string To,
    string Subject,
    string Body,
    ReminderStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? Error);

public sealed record Outcome(bool Ok, string Message);

public sealed record PaymentOutcome(bool Ok, string Message, Payment? Payment, bool Replayed = false);

public sealed record AgingBucket(string Label, int Invoices, long BalanceCents);

public sealed record WeekExpected(DateOnly WeekStart, long BalanceCents, int Invoices);

public sealed record ClientSummary(Guid ClientId, string Name, int OpenInvoices, long OutstandingCents, long OverdueCents);

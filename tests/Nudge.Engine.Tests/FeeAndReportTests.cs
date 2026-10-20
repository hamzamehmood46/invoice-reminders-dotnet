using Nudge.Engine;

namespace Nudge.Engine.Tests;

public class LateFeeTests
{
    private static Shop WithFee(LateFeePolicy fee) => new(new ReminderPolicy(ReminderPolicy.Default.Steps, fee));

    [Fact]
    public void The_default_policy_adds_one_and_a_half_percent_after_fourteen_days_past_due()
    {
        var s = new Shop();
        var id = s.Sent(100_000);
        s.Days(30 + 13);
        Assert.Equal(0, s.Billing.ApplyLateFees());

        s.Days(1);
        var charged = s.Billing.ApplyLateFees();

        Assert.Equal(1, charged);
        Assert.Equal(1_500, s.Get(id).LateFeeCents);
        Assert.Equal(101_500, s.Get(id).TotalCents);
        Assert.Equal(101_500, s.Get(id).BalanceCents);
    }

    [Fact]
    public void The_fee_is_charged_only_once_however_often_it_runs_and_however_late_it_gets()
    {
        var s = new Shop();
        var id = s.Sent(100_000);
        s.Days(44);

        for (var i = 0; i < 5; i++)
        {
            s.Billing.ApplyLateFees();
            s.Days(10);
        }

        Assert.Equal(1_500, s.Get(id).LateFeeCents);
    }

    [Fact]
    public void A_flat_fee_is_charged_as_stated()
    {
        var s = WithFee(new LateFeePolicy(LateFeeKind.Flat, 2_500, 0, 7, 0));
        var id = s.Sent(40_000);
        s.Days(37);

        s.Billing.ApplyLateFees();

        Assert.Equal(2_500, s.Get(id).LateFeeCents);
    }

    [Fact]
    public void A_percentage_fee_is_capped()
    {
        var s = new Shop();
        var id = s.Sent(10_000_000);
        s.Days(44);

        s.Billing.ApplyLateFees();

        Assert.Equal(50_000, s.Get(id).LateFeeCents);
    }

    [Fact]
    public void The_percentage_is_taken_of_what_is_still_owed_and_rounded_half_up()
    {
        var s = new Shop();
        var id = s.Sent(100_000);
        s.Pay(id, 99_667);
        s.Days(44);

        s.Billing.ApplyLateFees();

        Assert.Equal(5, s.Get(id).LateFeeCents);
    }

    [Fact]
    public void A_paid_disputed_draft_or_not_yet_late_enough_invoice_gets_no_fee()
    {
        var s = new Shop();
        var paid = s.Sent();
        var disputed = s.Sent();
        var draft = s.Draft();
        s.Pay(paid, 100_000);
        s.Billing.SetDisputed(disputed, true);
        s.Days(50);

        Assert.Equal(0, s.Billing.ApplyLateFees());

        Assert.Equal(0, s.Get(paid).LateFeeCents);
        Assert.Equal(0, s.Get(disputed).LateFeeCents);
        Assert.Equal(0, s.Get(draft).LateFeeCents);
    }

    [Fact]
    public void No_policy_means_no_fee()
    {
        var s = WithFee(LateFeePolicy.None);
        var id = s.Sent();
        s.Days(100);

        Assert.Equal(0, s.Billing.ApplyLateFees());
        Assert.Equal(0, s.Get(id).LateFeeCents);
    }

    [Fact]
    public void Once_a_fee_is_added_the_invoice_is_not_paid_until_the_fee_is_paid_too()
    {
        var s = new Shop();
        var id = s.Sent(100_000);
        s.Days(44);
        s.Billing.ApplyLateFees();

        s.Pay(id, 100_000);
        Assert.Equal(InvoiceStatus.PartiallyPaid, s.Get(id).Status);
        Assert.Equal(1_500, s.Get(id).BalanceCents);

        s.Pay(id, 1_500, "fee");
        Assert.Equal(InvoiceStatus.Paid, s.Get(id).Status);
    }

    [Fact]
    public void A_disputed_invoice_gets_its_fee_once_the_dispute_clears_if_still_late()
    {
        var s = new Shop();
        var id = s.Sent();
        s.Billing.SetDisputed(id, true);
        s.Days(50);
        s.Billing.ApplyLateFees();
        Assert.Equal(0, s.Get(id).LateFeeCents);

        s.Billing.SetDisputed(id, false);
        s.Billing.ApplyLateFees();

        Assert.Equal(1_500, s.Get(id).LateFeeCents);
    }
}

public class ReportTests
{
    private readonly Shop _s = new();

    private AgingBucket Bucket(string label) => _s.Billing.Aging().Single(b => b.Label == label);

    [Theory]
    [InlineData(-5, "Not yet due")]
    [InlineData(0, "Not yet due")]
    [InlineData(1, "1-30 days")]
    [InlineData(30, "1-30 days")]
    [InlineData(31, "31-60 days")]
    [InlineData(60, "31-60 days")]
    [InlineData(61, "61-90 days")]
    [InlineData(90, "61-90 days")]
    [InlineData(91, "Over 90 days")]
    [InlineData(400, "Over 90 days")]
    public void An_invoice_lands_in_the_right_aging_bucket_at_each_boundary(int daysPastDue, string expected)
    {
        _s.Sent(50_000);
        _s.Days(30 + daysPastDue);

        var bucket = Bucket(expected);

        Assert.Equal(1, bucket.Invoices);
        Assert.Equal(50_000, bucket.BalanceCents);
        Assert.Equal(1, _s.Billing.Aging().Sum(b => b.Invoices));
    }

    [Fact]
    public void Aging_counts_what_is_still_owed_and_ignores_paid_draft_and_voided_invoices()
    {
        var partial = _s.Sent(100_000);
        _s.Pay(partial, 30_000);
        var paid = _s.Sent(20_000);
        _s.Pay(paid, 20_000, "x");
        _s.Draft();
        var voided = _s.Sent();
        _s.Billing.Void(voided);

        var report = _s.Billing.Aging();

        Assert.Equal(70_000, report.Sum(b => b.BalanceCents));
        Assert.Equal(1, report.Sum(b => b.Invoices));
    }

    [Fact]
    public void The_five_buckets_always_appear_in_order_even_when_empty()
    {
        Assert.Equal(["Not yet due", "1-30 days", "31-60 days", "61-90 days", "Over 90 days"], _s.Billing.Aging().Select(b => b.Label));
    }

    [Fact]
    public void The_forecast_groups_open_balances_by_the_week_they_fall_due_starting_on_monday()
    {
        _s.Sent(10_000);
        _s.Days(3);
        _s.Sent(20_000);
        _s.Days(10);
        _s.Sent(40_000);

        var weeks = _s.Billing.Forecast(8);

        Assert.Equal(8, weeks.Count);
        // Today is Sunday Oct 25, so the current week started Monday Oct 19. The first two invoices fall due Nov 11 and
        // Nov 14 (both in the week of Nov 9) and the third on Nov 24 (the week of Nov 23).
        Assert.Equal(new DateOnly(2026, 10, 19), weeks[0].WeekStart);
        Assert.All(weeks, w => Assert.Equal(DayOfWeek.Monday, w.WeekStart.DayOfWeek));
        Assert.Equal(new long[] { 0, 0, 0, 30_000, 0, 40_000, 0, 0 }, weeks.Select(w => w.BalanceCents));
    }

    [Fact]
    public void Anything_already_late_is_counted_in_the_current_week_and_far_future_is_left_out()
    {
        var late = _s.Sent(10_000);
        _s.Days(60);
        var future = _s.Sent(99_000);
        _s.Days(0);
        var farTerms = new Shop(terms: 180);
        farTerms.Sent(50_000);

        var weeks = _s.Billing.Forecast(8);

        Assert.Equal(10_000, weeks[0].BalanceCents);
        Assert.Equal(99_000, weeks.Sum(w => w.BalanceCents) - 10_000);
        Assert.Equal(0, farTerms.Billing.Forecast(8).Sum(w => w.BalanceCents));
        _ = (late, future);
    }

    [Fact]
    public void Client_summaries_split_outstanding_from_overdue()
    {
        var other = _s.Billing.AddClient("Beta Co", "b@beta.com", 15);
        _s.Sent(10_000);
        _s.Sent(5_000, client: other);
        _s.Days(20);

        var summaries = _s.Billing.ClientSummaries();

        var acme = summaries.Single(c => c.Name == "Acme");
        var beta = summaries.Single(c => c.Name == "Beta Co");
        Assert.Equal((1, 10_000L, 0L), (acme.OpenInvoices, acme.OutstandingCents, acme.OverdueCents));
        Assert.Equal((1, 5_000L, 5_000L), (beta.OpenInvoices, beta.OutstandingCents, beta.OverdueCents));
    }
}

public class PropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void After_random_activity_the_books_always_balance_and_nobody_is_chased_twice_for_the_same_step(int seed)
    {
        var random = new Random(seed);
        var s = new Shop();
        var clients = new List<Guid> { s.Client, s.Billing.AddClient("B", "b@b.com", 14), s.Billing.AddClient("C", "c@c.com", 45) };
        var invoices = new List<Guid>();
        var keys = 0;

        for (var step = 0; step < 600; step++)
        {
            switch (random.Next(8))
            {
                case 0:
                case 1:
                    invoices.Add(s.Sent(random.Next(1, 500) * 100L, random.Next(0, 11) * 100, clients[random.Next(clients.Count)]));
                    break;
                case 2 when invoices.Count > 0:
                    var id = invoices[random.Next(invoices.Count)];
                    var balance = s.Get(id).BalanceCents;
                    s.Pay(id, balance == 0 ? 100 : random.Next(1, (int)Math.Min(balance, 50_000) + 1), "k" + keys++);
                    break;
                case 3:
                    s.Days(random.Next(1, 12));
                    break;
                case 4:
                    s.Billing.RunReminders();
                    s.Billing.SendEmails(s.Mail);
                    break;
                case 5:
                    s.Billing.ApplyLateFees();
                    break;
                case 6 when invoices.Count > 0:
                    s.Billing.SetDisputed(invoices[random.Next(invoices.Count)], random.Next(2) == 0);
                    break;
                case 7 when invoices.Count > 0:
                    s.Billing.Void(invoices[random.Next(invoices.Count)]);
                    break;
            }

            foreach (var invoice in s.Billing.Invoices())
            {
                Assert.True(invoice.PaidCents <= invoice.TotalCents);
                if (invoice.Status is InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid)
                {
                    Assert.Equal(invoice.TotalCents - invoice.PaidCents, invoice.BalanceCents);
                    Assert.True(invoice.BalanceCents > 0);
                }

                if (invoice.Status == InvoiceStatus.Paid)
                {
                    Assert.Equal(invoice.TotalCents, invoice.PaidCents);
                    Assert.Equal(0, invoice.BalanceCents);
                }
            }
        }

        var perStep = s.Billing.Reminders().Where(r => r.Status != ReminderStatus.Skipped).GroupBy(r => (r.InvoiceId, r.StepIndex));
        Assert.All(perStep, g => Assert.Single(g));
        Assert.Equal(s.Billing.Aging().Sum(b => b.BalanceCents), s.Billing.Invoices().Where(i => i.IsOpen).Sum(i => i.BalanceCents));
    }
}

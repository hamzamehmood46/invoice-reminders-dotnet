using Nudge.Engine;

namespace Nudge.Engine.Tests;

public class ReminderTests
{
    private readonly Shop _s = new();

    private IReadOnlyList<Reminder> Active(Guid id) => _s.Reminders(id).Where(r => r.Status != ReminderStatus.Skipped).ToList();

    [Theory]
    [InlineData(-3, Tone.Friendly)]
    [InlineData(0, Tone.DueToday)]
    [InlineData(3, Tone.Gentle)]
    [InlineData(7, Tone.Firm)]
    [InlineData(14, Tone.Final)]
    public void Each_step_goes_out_on_exactly_its_day(int daysFromDue, Tone tone)
    {
        var id = _s.Sent();
        _s.Days(30 + daysFromDue - 1);
        _s.Billing.RunReminders();
        var before = _s.Reminders(id).Count(r => r.Tone == tone);

        _s.Days(1);
        _s.Billing.RunReminders();

        Assert.Equal(0, before);
        Assert.Contains(_s.Reminders(id), r => r.Tone == tone && r.Status == ReminderStatus.Queued);
    }

    [Fact]
    public void Nothing_is_sent_before_the_first_step()
    {
        _s.Sent();
        _s.Days(26);

        Assert.Equal(0, _s.Billing.RunReminders());
    }

    [Fact]
    public void Running_the_reminders_again_the_same_day_sends_nothing_new()
    {
        var id = _s.Sent();
        _s.Days(27);

        _s.Billing.RunReminders();
        _s.Billing.RunReminders();
        _s.Billing.RunReminders();

        Assert.Single(_s.Reminders(id));
    }

    [Fact]
    public void Walking_through_a_late_invoice_day_by_day_sends_each_reminder_exactly_once_in_order()
    {
        var id = _s.Sent();

        for (var day = 0; day < 50; day++)
        {
            _s.Billing.RunReminders();
            _s.Days(1);
        }

        Assert.Equal([Tone.Friendly, Tone.DueToday, Tone.Gentle, Tone.Firm, Tone.Final], _s.Reminders(id).Select(r => r.Tone));
        Assert.All(_s.Reminders(id), r => Assert.Equal(ReminderStatus.Queued, r.Status));
    }

    [Fact]
    public void If_several_steps_were_missed_only_the_latest_is_sent_and_the_earlier_ones_are_marked_skipped()
    {
        var id = _s.Sent();
        _s.Days(38);

        _s.Billing.RunReminders();

        var reminders = _s.Reminders(id);
        Assert.Equal(4, reminders.Count);
        Assert.Equal(Tone.Firm, Assert.Single(Active(id)).Tone);
        Assert.Equal([Tone.Friendly, Tone.DueToday, Tone.Gentle], reminders.Where(r => r.Status == ReminderStatus.Skipped).Select(r => r.Tone));
        Assert.All(reminders.Where(r => r.Status == ReminderStatus.Skipped), r => Assert.Contains("Superseded", r.Error));
    }

    [Fact]
    public void A_skipped_step_is_never_sent_later()
    {
        var id = _s.Sent();
        _s.Days(38);
        _s.Billing.RunReminders();

        _s.Days(1);
        _s.Billing.RunReminders();

        Assert.Single(_s.Reminders(id), r => r.Tone == Tone.Firm);
        Assert.Single(_s.Reminders(id), r => r.Tone == Tone.Gentle);
    }

    [Fact]
    public void A_paid_invoice_gets_no_further_reminders()
    {
        var id = _s.Sent();
        _s.Days(33);
        _s.Billing.RunReminders();
        _s.Pay(id, 100_000);

        _s.Days(20);
        _s.Billing.RunReminders();

        Assert.DoesNotContain(_s.Reminders(id), r => r.Tone is Tone.Firm or Tone.Final);
    }

    [Fact]
    public void A_partially_paid_invoice_keeps_being_chased_for_what_is_left()
    {
        var id = _s.Sent(100_000);
        _s.Pay(id, 60_000);
        _s.Days(33);

        _s.Billing.RunReminders();

        var body = Assert.Single(Active(id)).Body;
        Assert.Contains("$400.00", body);
        Assert.DoesNotContain("$1,000.00", body);
    }

    [Fact]
    public void Draft_and_voided_invoices_are_never_chased()
    {
        _s.Draft();
        var voided = _s.Sent();
        _s.Billing.Void(voided);
        _s.Days(60);

        Assert.Equal(0, _s.Billing.RunReminders());
    }

    [Fact]
    public void A_client_with_reminders_paused_gets_none_and_resumes_with_the_current_step_only()
    {
        var id = _s.Sent();
        _s.Billing.PauseReminders(_s.Client, true);
        _s.Days(33);
        Assert.Equal(0, _s.Billing.RunReminders());

        _s.Billing.PauseReminders(_s.Client, false);
        _s.Billing.RunReminders();

        Assert.Equal(Tone.Gentle, Assert.Single(Active(id)).Tone);
    }

    [Fact]
    public void A_disputed_invoice_is_not_chased_until_the_dispute_is_cleared()
    {
        var id = _s.Sent();
        _s.Billing.SetDisputed(id, true);
        _s.Days(40);
        Assert.Equal(0, _s.Billing.RunReminders());

        _s.Billing.SetDisputed(id, false);
        _s.Billing.RunReminders();

        Assert.Equal(Tone.Firm, Assert.Single(Active(id)).Tone);
    }

    [Fact]
    public void Each_invoice_has_its_own_schedule()
    {
        var early = _s.Sent();
        _s.Days(20);
        var later = _s.Sent();
        _s.Days(10);

        _s.Billing.RunReminders();

        Assert.Equal(Tone.DueToday, Assert.Single(Active(early)).Tone);
        Assert.Empty(Active(later));
    }

    [Fact]
    public void Messages_name_the_invoice_the_amount_and_the_due_date_and_get_firmer()
    {
        var id = _s.Sent(250_000);
        _s.Days(27);
        _s.Billing.RunReminders();
        var friendly = Active(id).Single();

        Assert.Contains("INV-0001", friendly.Subject);
        Assert.Contains("$2,500.00", friendly.Body);
        Assert.Contains("November 11", friendly.Body);
        Assert.Equal("ap@acme.com", friendly.To);
    }

    [Fact]
    public void Firm_and_final_notices_mention_the_late_fee_and_the_gentle_ones_do_not()
    {
        var id = _s.Sent();
        _s.Days(33);
        _s.Billing.RunReminders();
        var gentle = _s.Reminders(id).Single(r => r.Tone == Tone.Gentle);
        _s.Days(4);
        _s.Billing.RunReminders();
        var firm = _s.Reminders(id).Single(r => r.Tone == Tone.Firm);

        Assert.DoesNotContain("late fee", gentle.Body);
        Assert.Contains("late fee", firm.Body);
        Assert.Contains("7 days overdue", firm.Body);
    }

    [Fact]
    public void A_custom_schedule_is_followed()
    {
        var shop = new Shop(new ReminderPolicy([new(5, Tone.Gentle), new(20, Tone.Final)], LateFeePolicy.None));
        var id = shop.Sent();

        shop.Days(34);
        shop.Billing.RunReminders();
        Assert.Empty(shop.Reminders(id));
        shop.Days(1);
        shop.Billing.RunReminders();

        Assert.Equal([Tone.Gentle], shop.Reminders(id).Select(r => r.Tone));
    }

    [Fact]
    public void Parallel_runs_never_queue_the_same_step_twice()
    {
        var id = _s.Sent();
        _s.Days(40);

        Parallel.For(0, 40, _ => _s.Billing.RunReminders());

        Assert.Single(Active(id));
    }
}

public class EmailTests
{
    private readonly Shop _s = new();

    private Guid Overdue()
    {
        var id = _s.Sent();
        _s.Days(33);
        _s.Billing.RunReminders();
        return id;
    }

    [Fact]
    public void Queued_reminders_are_emailed_once()
    {
        var id = Overdue();

        Assert.Equal(1, _s.Billing.SendEmails(_s.Mail));
        Assert.Equal(0, _s.Billing.SendEmails(_s.Mail));

        Assert.Single(_s.Mail.Sent);
        Assert.Equal(ReminderStatus.Sent, _s.Reminders(id).Single().Status);
        Assert.Equal("ap@acme.com", _s.Mail.Sent[0].To);
    }

    [Fact]
    public void A_failed_send_is_retried_after_five_minutes_then_thirty_and_then_given_up_on()
    {
        var id = Overdue();
        _s.Mail.Mode = MailMode.Down;

        _s.Billing.SendEmails(_s.Mail);
        var first = _s.Reminders(id).Single();
        Assert.Equal(1, first.Attempts);
        Assert.Equal(_s.Clock.GetUtcNow().AddMinutes(5), first.NextAttemptAt);

        _s.Clock.Advance(TimeSpan.FromMinutes(4));
        _s.Billing.SendEmails(_s.Mail);
        Assert.Equal(1, _s.Reminders(id).Single().Attempts);

        _s.Clock.Advance(TimeSpan.FromMinutes(1));
        _s.Billing.SendEmails(_s.Mail);
        var second = _s.Reminders(id).Single();
        Assert.Equal(2, second.Attempts);
        Assert.Equal(_s.Clock.GetUtcNow().AddMinutes(30), second.NextAttemptAt);

        _s.Clock.Advance(TimeSpan.FromMinutes(30));
        _s.Billing.SendEmails(_s.Mail);
        var last = _s.Reminders(id).Single();
        Assert.Equal(ReminderStatus.Failed, last.Status);
        Assert.Equal(3, last.Attempts);
        Assert.Contains("not answering", last.Error);
    }

    [Fact]
    public void A_retry_succeeds_when_the_mail_server_comes_back()
    {
        var id = Overdue();
        _s.Mail.Mode = MailMode.Down;
        _s.Billing.SendEmails(_s.Mail);
        _s.Mail.Mode = MailMode.Up;
        _s.Clock.Advance(TimeSpan.FromMinutes(5));

        _s.Billing.SendEmails(_s.Mail);

        Assert.Equal(ReminderStatus.Sent, _s.Reminders(id).Single().Status);
        Assert.Single(_s.Mail.Sent);
    }

    [Fact]
    public void A_reminder_for_an_invoice_paid_in_the_meantime_is_dropped_not_sent()
    {
        var id = Overdue();
        _s.Pay(id, 100_000);

        Assert.Equal(0, _s.Billing.SendEmails(_s.Mail));

        Assert.Empty(_s.Mail.Sent);
        Assert.Equal(ReminderStatus.Skipped, _s.Reminders(id).Single().Status);
    }

    [Fact]
    public void A_reminder_for_an_invoice_that_became_disputed_is_dropped()
    {
        var id = Overdue();
        _s.Billing.SetDisputed(id, true);

        _s.Billing.SendEmails(_s.Mail);

        Assert.Empty(_s.Mail.Sent);
    }

    [Fact]
    public void Skipped_reminders_are_never_emailed()
    {
        _s.Sent();
        _s.Days(45);
        _s.Billing.RunReminders();

        _s.Billing.SendEmails(_s.Mail);

        Assert.Single(_s.Mail.Sent);
        Assert.Contains("Final notice", _s.Mail.Sent[0].Subject);
    }
}

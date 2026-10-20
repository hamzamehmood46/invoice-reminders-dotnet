# Nudge: invoices that get paid

[![Test and deploy](https://github.com/hamzamehmood46/invoice-reminders-dotnet/actions/workflows/pages.yml/badge.svg)](https://github.com/hamzamehmood46/invoice-reminders-dotnet/actions/workflows/pages.yml)

**Live demo: [hamzamehmood46.github.io/invoice-reminders-dotnet](https://hamzamehmood46.github.io/invoice-reminders-dotnet/)**

An invoicing and payment-reminder engine in C#. It sends the right reminder on the right day (friendly, due today, gentle, firm, final), adds a late fee once, never counts the same payment twice, and stops chasing the moment an invoice is paid, disputed or voided. Blazor WebAssembly runs the same engine in your browser, on a clock you control, so you can watch weeks of collections in a minute.

## Try it (3 minutes)

1. **Invoices:** several are already overdue. Press **+1 week** and watch statuses and reminders escalate. A late fee is added once, never twice.
2. Press **Pay in full** on an invoice, then **Retry the same payment**: the repeated payment event is recognized and counted once. **Pay 150%** is refused as an overpayment.
3. **Reminders:** take the mail server down, move a day, and watch the send retry after 5 minutes, then 30, then give up.
4. Mark an invoice **Disputed**: reminders and late fees stop until it is cleared.
5. **Reports:** the aging report, an 8-week cash forecast and per-client totals update as you go.

## What it does

| Capability | Behavior |
|---|---|
| **Invoices** | Draft, sent, partially paid, paid, void. Line items, tax in basis points rounded half up to the cent, due date from the client's payment terms, and a history of every step. |
| **Payments** | Partial and full payments, with an idempotency key per payment event: a retried webhook returns the original and never counts twice, even after the invoice is paid off. Overpayments, zero or negative amounts and payments on drafts or voided invoices are refused. Parallel deliveries of the same payment record exactly one. |
| **Reminder schedule** | Steps relative to the due date (default -3, 0, +3, +7, +14 days) with a tone each. Every step is queued once per invoice, however often the job runs. If days were skipped, only the latest due step is sent and earlier ones are marked skipped, never sent late. |
| **Stops when it should** | Paid, voided, draft, disputed invoices and clients with reminders paused get nothing. A queued reminder for an invoice paid or disputed in the meantime is dropped instead of sent. |
| **Late fees** | Percentage (with a cap) or flat, after a grace period, charged once, on what is still owed. A disputed invoice gets its fee only once cleared and still late. |
| **Email delivery** | Failed sends retry after 5 minutes, then 30, then are marked failed with the reason. |
| **Reports** | Aging buckets (not due, 1-30, 31-60, 61-90, over 90), an 8-week forecast grouped by Monday-start weeks, and a per-client summary. |

## How it is built

```
src/Nudge.Engine   C#: billing, reminder scheduling, fees, reports, fake email sender, simulated clock. No UI.
src/Nudge.Web      Blazor WebAssembly front end (runs the same engine in the browser)
tests/             xUnit tests
```

The clock and the mail server are injected, so every time-based rule is tested by moving a simulated clock rather than waiting.

## Tests

```bash
dotnet test
```

93 tests, including:

- tax rounding at every half-cent edge and money formatting;
- each reminder step firing on exactly its day and not a day before, missed steps being superseded, and a skipped step never being sent later;
- payments: partial, several, overpayment, idempotent replays, replay after payoff, and 200 parallel one-cent payments on a one-dollar invoice never overpaying;
- the email retry schedule to the minute, recovery when the server returns, and dropping reminders for invoices that were paid or disputed in the meantime;
- late fee thresholds, once-only charging, cap, rounding, and the fee needing payment before an invoice counts as paid;
- aging bucket boundaries at 0, 1, 30, 31, 60, 61, 90 and 91 days, and the weekly forecast;
- a randomized test: 600 random actions across five seeds, after each of which the books must balance and no invoice step may have been chased twice.

I also broke the rules on purpose to check the tests notice: tax rounding down, allowing overpayment, not recognizing payment replays, letting a step repeat, not superseding missed steps, chasing disputed invoices, repeating the late fee, an off-by-one fee threshold, retrying email with no delay, reminding paid invoices, voiding an invoice with payments, an aging boundary off by one, an exact payment never marking paid, rounding the fee down, ignoring the fee cap, and chasing paused clients. Each of the 16 made tests fail.

## Scope

This is a demonstration of the engine and its rules. Data lives in memory (reloading the page resets it), email goes to a fake sender, and there is no real payment provider or login.

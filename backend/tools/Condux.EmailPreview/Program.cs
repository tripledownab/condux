using Condux.Core.WeeklySummaries;
using Condux.Notifications;

// Dev tool for iterating on the email look and feel: renders each styled email through EmailLayout and
// sends it to a local SMTP catcher (Mailpit by default) so you can eyeball it in the Mailpit web inbox.
//
//   dotnet run --project backend/tools/Condux.EmailPreview [recipient@example.com]
//
// Needs a Mailpit (or any SMTP) reachable at MAILPIT_SMTP_HOST:MAILPIT_SMTP_PORT (default localhost:1025).
// Bring one up either with the dev stack (`pnpm dev:stack`) or standalone:
//   docker run --rm -p 8025:8025 -p 1025:1025 axllent/mailpit
// then open http://localhost:8025 after running this.

var host = Environment.GetEnvironmentVariable("MAILPIT_SMTP_HOST") ?? "localhost";
var port = int.TryParse(Environment.GetEnvironmentVariable("MAILPIT_SMTP_PORT"), out var parsed) ? parsed : 1025;
var recipient = args.Length > 0 ? args[0] : "preview@condux.dev";

var options = new SmtpOptions(host, port, "alerts@condux.dev", User: null, Password: null, UseSsl: false);
using var sender = new SystemSmtpSender(options);

// A realistic weekly digest, rendered through the real builders (ADR-0031) so the preview exercises the
// actual subject/body/HTML, not a hand-written mock.
const string weeklyUrl = "https://app.condux.ai";
var weekly = new WeeklySummary(
    OrgId: 1, OrgName: "Acme",
    WeekStart: DateTimeOffset.UtcNow.AddDays(-7), WeekEnd: DateTimeOffset.UtcNow,
    Events: 18432, PreviousEvents: 15120,
    NewIssues: 12, Regressions: 3, Resolved: 8, OpenIssues: 41, UsersAffected: 1204,
    TopIssues:
    [
        new TopIssue("TypeError: undefined is not a function", 4231),
        new TopIssue("NullReferenceException in CheckoutService.Pay", 2890),
        new TopIssue("Timeout calling the payments provider", 1502),
    ],
    Fixes: new WeeklyFixActivity(Proposed: 5, PrsOpened: 4, PrsMerged: 2, AutoResolved: 1));

var samples = new (string Subject, string PlainText, EmailContent Content)[]
{
    (
        WeeklySummaryText.Subject(weekly),
        WeeklySummaryText.Body(weekly, weeklyUrl),
        WeeklySummaryEmail.Content(weekly, weeklyUrl)),
    (
        "[Condux] New issue [Error] TypeError: undefined is not a function",
        "New issue [Error] TypeError: undefined is not a function\n"
            + "Culprit: src/checkout/pay.ts:42\nProject: checkout-api",
        new EmailContent(
            Heading: "TypeError: undefined is not a function",
            Paragraphs: ["A new issue was reported in your project."],
            Facts:
            [
                new EmailFact("Culprit", "src/checkout/pay.ts:42"),
                new EmailFact("Project", "checkout-api"),
                new EmailFact("Issue", "0192f0a1-7c3e-7b9a-9f21-2e6b1a4c8d55"),
            ],
            Badge: "New issue · Error",
            BadgeColorHex: "#dc2626")),
    (
        "Condux: auto-fix paused (monthly spend cap reached)",
        "Auto-fix for Acme is paused because this month's Conductor spend has reached the configured cost cap.",
        new EmailContent(
            Heading: "auto-fix paused (monthly spend cap reached)",
            Paragraphs:
            [
                "Auto-fix for Acme is paused because this month's Conductor spend has reached the configured "
                    + "cost cap. New errors will not get an automatic fix PR until that clears.",
                "You can still request fixes manually from the dashboard.",
            ])),
    (
        "You are invited to join Acme on Condux",
        "boss@acme.io invited you to join Acme on Condux as admin.\n"
            + "Accept: https://app.condux.ai/invite?token=demo-token-123",
        new EmailContent(
            Heading: "You are invited to join Acme",
            Paragraphs:
            [
                "boss@acme.io invited you to join Acme on Condux as admin.",
                "Sign in or create your Condux account with this email address to join.",
                "This invitation expires in 7 days. If you were not expecting it, you can ignore this email.",
            ],
            Button: new EmailButton("Accept invitation", "https://app.condux.ai/invite?token=demo-token-123"))),
};

foreach (var sample in samples)
{
    using var message = EmailLayout.CreateMessage(
        options.From, recipient, sample.Subject, sample.PlainText, sample.Content);
    await sender.SendAsync(message);
    Console.WriteLine($"sent: {sample.Subject}");
}

Console.WriteLine(
    $"Done. Sent {samples.Length} emails to {recipient} via {host}:{port}. Open Mailpit: http://localhost:8025");

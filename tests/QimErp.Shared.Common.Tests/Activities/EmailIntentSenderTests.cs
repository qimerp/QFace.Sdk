using FluentAssertions;
using QimErp.Shared.Common.Activities.People;
using QimErp.Shared.Common.Services.Notifications;
using QimErp.Shared.Common.Services.Notifications.Intents;
using Xunit;

namespace QimErp.Shared.Common.Tests.Activities;

public sealed class EmailIntentSenderTests
{
    [Fact]
    public void The_request_drops_the_query_and_empty_fields_and_carries_no_tenant()
    {
        var employeeId = Guid.CreateVersion7();
        var request = EmailIntentRequests.From(new EmailIntentMessage(
            NotificationIntents.People.DocumentExpiring.Key, 1, "occ", new EmailIntentRecipient(employeeId, " ama@tenant.test ", "Ama"),
            "/hr/employees/x?tab=documents", new Dictionary<string, string> { ["DocumentName"] = " Passport ", ["Other"] = " " },
            new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Unspecified)));

        request.RecipientId.Should().Be(employeeId);
        request.RecipientEmployeeId.Should().Be(employeeId);
        request.RecipientEmail.Should().Be("ama@tenant.test");
        request.LinkPath.Should().Be("/hr/employees/x");
        request.Fields.Should().Equal(new Dictionary<string, string> { ["DocumentName"] = "Passport" });
        request.OccurredAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        EmailIntentRequests.LinkPath("relative").Should().Be("/");
        typeof(PeopleEmailIntentRequest).GetProperty("TenantId").Should().BeNull();
    }

    [Fact]
    public void A_mailbox_recipient_uses_the_stable_mailbox_id()
    {
        var recipient = EmailIntentRecipient.Mailbox(" Tax@Vendor.test ");
        var request = EmailIntentRequests.From(new EmailIntentMessage(
            NotificationIntents.Finance.WhtCertificateReady.Key, NotificationIntents.Finance.WhtCertificateReady.V1,
            Guid.CreateVersion7().ToString("N"), recipient, "/documents/wht-certificates/abc",
            new Dictionary<string, string> { ["CertificateNumber"] = "WHT-1" }, DateTime.UtcNow));

        request.RecipientId.Should().Be(PeopleEmailIntents.MailboxRecipientId("tax@vendor.test"));
        request.RecipientEmployeeId.Should().BeNull();
        request.RecipientEmail.Should().Be("Tax@Vendor.test");
        request.LinkPath.Should().Be("/documents/wht-certificates/abc");
    }
}

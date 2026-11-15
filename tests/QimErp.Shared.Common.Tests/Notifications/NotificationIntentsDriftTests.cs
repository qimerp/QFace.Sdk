using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using QimErp.Shared.Common.Services.Notifications;
using QimErp.Shared.Common.Services.Notifications.Intents;
using Xunit;

namespace QimErp.Shared.Common.Tests.Notifications;

public class NotificationIntentsDriftTests
{
    [Fact]
    public void RecomputedLockRevision_EqualsGeneratedContractRevision()
    {
        var compact = "{" + string.Join(",", NotificationIntentLock.Contracts
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c => $"\"{c.Key}\":\"{c.Value}\"")) + "}";

        var recomputed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(compact)));

        recomputed.Should().Be(NotificationIntentLock.Revision);
        NotificationIntents.ContractRevision.Should().Be(NotificationIntentLock.Revision);
    }

    [Fact]
    public void GeneratedIntents_AndLockEntries_MatchExactly()
    {
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in typeof(NotificationIntents).GetNestedTypes())
        foreach (var intent in module.GetNestedTypes())
        {
            var key = (string)intent.GetField("Key", BindingFlags.Public | BindingFlags.Static)!.GetRawConstantValue()!;
            foreach (var version in intent.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.Name.StartsWith('V')))
                generated.Add(NotificationIntentLock.Identity(key, (int)version.GetRawConstantValue()!));
        }

        generated.Should().NotBeEmpty();
        generated.Should().BeEquivalentTo(NotificationIntentLock.Contracts.Keys);
    }

    [Fact]
    public void FixtureIntentFiles_HashToTheirLockEntries()
    {
        var files = Directory.GetFiles(Path.Combine(NotificationFixtures.Root, "intents"), "*.json");
        files.Should().HaveCount(NotificationIntentLock.Contracts.Count);

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var split = name.LastIndexOf(".v", StringComparison.Ordinal);
            var key = name[..split];
            var version = int.Parse(name[(split + 2)..], System.Globalization.CultureInfo.InvariantCulture);

            NotificationIntentLock.TryGetContractSha256(key, version, out var locked).Should().BeTrue(name);
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))).Should().Be(locked, name);
        }
    }

    [Fact]
    public void Lock_CarriesAll50Contracts_IncludingLegacySenderIntents()
    {
        NotificationIntentLock.Contracts.Should().HaveCount(50);
        NotificationIntentLock.Contracts.Keys.Should().Contain(
        [
            NotificationIntentLock.Identity(NotificationIntents.People.LeaveRequestSubmitted.Key, NotificationIntents.People.LeaveRequestSubmitted.V1),
            NotificationIntentLock.Identity(NotificationIntents.People.LeaveRequestDecided.Key, NotificationIntents.People.LeaveRequestDecided.V1),
            NotificationIntentLock.Identity(NotificationIntents.People.LeaveCoverAssigned.Key, NotificationIntents.People.LeaveCoverAssigned.V1),
            NotificationIntentLock.Identity(NotificationIntents.People.SurveyInvited.Key, NotificationIntents.People.SurveyInvited.V1),
            NotificationIntentLock.Identity(NotificationIntents.People.SurveyReminded.Key, NotificationIntents.People.SurveyReminded.V1),
            NotificationIntentLock.Identity(NotificationIntents.Workflow.RequestStarted.Key, NotificationIntents.Workflow.RequestStarted.V1),
            NotificationIntentLock.Identity(NotificationIntents.Workflow.RequestStageAdvanced.Key, NotificationIntents.Workflow.RequestStageAdvanced.V1),
            NotificationIntentLock.Identity(NotificationIntents.Workflow.RequestApproved.Key, NotificationIntents.Workflow.RequestApproved.V1),
            NotificationIntentLock.Identity(NotificationIntents.Workflow.RequestRejected.Key, NotificationIntents.Workflow.RequestRejected.V1),
            NotificationIntentLock.Identity(NotificationIntents.Workflow.RequestReturned.Key, NotificationIntents.Workflow.RequestReturned.V1),
            NotificationIntentLock.Identity(NotificationIntents.Workflow.ApprovalOverdue.Key, NotificationIntents.Workflow.ApprovalOverdue.V1),
            NotificationIntentLock.Identity(NotificationIntents.Auth.AccountActivationRequested.Key, NotificationIntents.Auth.AccountActivationRequested.V1),
            NotificationIntentLock.Identity(NotificationIntents.Tenant.OwnerActivationRequested.Key, NotificationIntents.Tenant.OwnerActivationRequested.V1),
            NotificationIntentLock.Identity(NotificationIntents.Tenant.WorkspaceRequestApproved.Key, NotificationIntents.Tenant.WorkspaceRequestApproved.V1),
            NotificationIntentLock.Identity(NotificationIntents.Tenant.WorkspaceRequestRejected.Key, NotificationIntents.Tenant.WorkspaceRequestRejected.V1),
            NotificationIntentLock.Identity(NotificationIntents.Tenant.WorkspaceReady.Key, NotificationIntents.Tenant.WorkspaceReady.V1),
            NotificationIntentLock.Identity(NotificationIntents.Auth.PasswordChanged.Key, NotificationIntents.Auth.PasswordChanged.V1),
            NotificationIntentLock.Identity(NotificationIntents.Auth.PasswordAdminReset.Key, NotificationIntents.Auth.PasswordAdminReset.V1),
            NotificationIntentLock.Identity(NotificationIntents.Auth.SignInSucceeded.Key, NotificationIntents.Auth.SignInSucceeded.V1),
            NotificationIntentLock.Identity(NotificationIntents.Auth.MfaSetupReminder.Key, NotificationIntents.Auth.MfaSetupReminder.V1),
            NotificationIntentLock.Identity(NotificationIntents.Iam.AccessReviewReminder.Key, NotificationIntents.Iam.AccessReviewReminder.V1),
            NotificationIntentLock.Identity(NotificationIntents.Iam.AuditFollowUpAssigned.Key, NotificationIntents.Iam.AuditFollowUpAssigned.V1),
            NotificationIntentLock.Identity(NotificationIntents.AppStore.ItemInstalled.Key, NotificationIntents.AppStore.ItemInstalled.V1),
            NotificationIntentLock.Identity(NotificationIntents.Payroll.PayslipReady.Key, NotificationIntents.Payroll.PayslipReady.V1),
            NotificationIntentLock.Identity(NotificationIntents.Support.TicketReceived.Key, NotificationIntents.Support.TicketReceived.V1),
            NotificationIntentLock.Identity(NotificationIntents.Support.TicketReplied.Key, NotificationIntents.Support.TicketReplied.V1),
            NotificationIntentLock.Identity(NotificationIntents.Support.TicketResolved.Key, NotificationIntents.Support.TicketResolved.V1),
            NotificationIntentLock.Identity(NotificationIntents.Support.TicketAssigned.Key, NotificationIntents.Support.TicketAssigned.V1),
            NotificationIntentLock.Identity(NotificationIntents.Support.TicketQueued.Key, NotificationIntents.Support.TicketQueued.V1),
            NotificationIntentLock.Identity(NotificationIntents.Support.TicketSlaBreached.Key, NotificationIntents.Support.TicketSlaBreached.V1),
        ]);
    }

    [Fact]
    public void Lock_IsLoadedFromTheEmbeddedResource()
    {
        typeof(NotificationIntentLock).Assembly.GetManifestResourceNames().Should().Contain(NotificationIntentLock.ResourceName);
        NotificationIntentLock.TryGetContractSha256(NotificationIntents.Auth.PasswordResetRequested.Key, NotificationIntents.Auth.PasswordResetRequested.V1, out _)
            .Should().BeTrue();
        NotificationIntentLock.TryGetContractSha256("auth.password_reset.requested", 2, out _).Should().BeFalse();
    }

    [Fact]
    public void LockLoad_FailsFast_WhenRevisionDoesNotMatchContracts()
    {
        var body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(LockBytes()).Replace(NotificationIntentLock.Revision, new string('0', 64), StringComparison.Ordinal));

        var act = () => NotificationIntentLock.Load(body, new string('0', 64));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LockLoad_FailsFast_WhenGeneratedRevisionDiffers()
    {
        var act = () => NotificationIntentLock.Load(LockBytes(), new string('0', 64));

        act.Should().Throw<InvalidOperationException>();
        NotificationIntentLock.Load(LockBytes(), NotificationIntents.ContractRevision).Revision.Should().Be(NotificationIntentLock.Revision);
    }

    private static byte[] LockBytes()
    {
        using var stream = typeof(NotificationIntentLock).Assembly.GetManifestResourceStream(NotificationIntentLock.ResourceName)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

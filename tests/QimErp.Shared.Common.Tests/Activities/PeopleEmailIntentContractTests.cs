using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using QimErp.Shared.Common.Activities.People;
using QimErp.Shared.Common.Services.Notifications;
using QimErp.Shared.Common.Services.Notifications.Intents;
using Temporalio.Converters;
using Temporalio.Workflows;
using Xunit;

namespace QimErp.Shared.Common.Tests.Activities;

public sealed class PeopleEmailIntentContractTests
{
    private static PeopleEmailIntentRequest Sample() => new()
    {
        IntentKey = NotificationIntents.People.OnboardingTaskReminder.Key,
        IntentVersion = NotificationIntents.People.OnboardingTaskReminder.V1,
        OccurrenceId = "0193a3c4b5d6e7f8a9b0c1d2e3f4a5b6",
        RecipientId = Guid.Parse("0193a3c4-b5d6-7e7f-8a9b-0c1d2e3f4a5b"),
        RecipientEmployeeId = Guid.Parse("0193a3c4-b5d6-7e7f-8a9b-0c1d2e3f4a5b"),
        RecipientEmail = "owner@tenant.test",
        RecipientFirstName = "Ama",
        LinkPath = "/hr/onboarding/0193a3c4-b5d6-7e7f-8a9b-0c1d2e3f4a5c",
        Fields = new Dictionary<string, string> { ["TaskTitle"] = "Upload ID documents", ["Message"] = "Please look." },
        OccurredAtUtc = new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Request_round_trips_through_the_temporal_converter()
    {
        var converter = DataConverter.Default.PayloadConverter;
        var restored = converter.ToValue<PeopleEmailIntentRequest>(converter.ToPayload(Sample()));

        restored.Should().BeEquivalentTo(Sample());
    }

    [Fact]
    public void Request_carries_no_tenant()
    {
        typeof(PeopleEmailIntentRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().BeEquivalentTo("IntentKey", "IntentVersion", "OccurrenceId", "RecipientId", "RecipientEmployeeId",
                "RecipientEmail", "RecipientFirstName", "LinkPath", "Fields", "OccurredAtUtc");
    }

    [Fact]
    public void Request_without_optional_fields_reads_nulls()
    {
        var converter = DataConverter.Default.PayloadConverter;
        var payload = converter.ToPayload(new { IntentKey = "people.news.published", IntentVersion = 1, OccurrenceId = "x", RecipientId = Guid.NewGuid() });

        var restored = converter.ToValue<PeopleEmailIntentRequest>(payload);

        restored.RecipientEmployeeId.Should().BeNull();
        restored.RecipientEmail.Should().BeNull();
        restored.LinkPath.Should().Be("/");
        restored.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Workflow_name_queue_and_run_parameter_are_pinned()
    {
        typeof(IPeopleEmailIntentWorkflow).GetCustomAttribute<WorkflowAttribute>()!.Name.Should().Be("PeopleEmailIntentWorkflow");
        PeopleEmailIntents.TaskQueue.Should().Be("qimerp-platform-people-email");
        PeopleEmailIntents.Audience.Should().Be("employee");
        PeopleEmailIntents.RecipientReference.Should().Be("recipientId");
        typeof(IPeopleEmailIntentWorkflow).GetMethods()
            .Single(m => m.GetCustomAttribute<WorkflowRunAttribute>() is not null)
            .GetParameters().Single().ParameterType.Should().Be(typeof(PeopleEmailIntentRequest));
    }

    [Fact]
    public void Workflow_id_is_stable_and_differs_by_occurrence_recipient_and_intent()
    {
        var recipient = Guid.NewGuid();
        var id = PeopleEmailIntents.WorkflowId("people.news.published", 1, "occ-1", recipient);

        id.Should().MatchRegex("^people-email-[0-9a-f]{32}$");
        PeopleEmailIntents.WorkflowId("people.news.published", 1, "occ-1", recipient).Should().Be(id);
        PeopleEmailIntents.WorkflowId("people.news.published", 1, "occ-2", recipient).Should().NotBe(id);
        PeopleEmailIntents.WorkflowId("people.news.published", 1, "occ-1", Guid.NewGuid()).Should().NotBe(id);
        PeopleEmailIntents.WorkflowId("people.leave_plan.nudged", 1, "occ-1", recipient).Should().NotBe(id);
    }

    [Fact]
    public void Mailbox_recipient_id_ignores_case_and_surrounding_space()
    {
        var id = PeopleEmailIntents.MailboxRecipientId("hr@tenant.test");

        PeopleEmailIntents.MailboxRecipientId(" HR@Tenant.test ").Should().Be(id);
        PeopleEmailIntents.MailboxRecipientId("people@tenant.test").Should().NotBe(id);
        id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public void Every_people_intent_is_locked()
    {
        var keys = typeof(NotificationIntents.People).GetNestedTypes()
            .Select(t => (string)t.GetField("Key")!.GetRawConstantValue()!)
            .ToList();

        keys.Should().HaveCount(15);
        keys.Where(key => !NotificationIntentLock.TryGetContractSha256(key, 1, out _)).Should().BeEmpty();
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using QimErp.Shared.Common.Security;
using QimErp.Shared.Common.Services.Notifications.Rendering;
using Xunit;

namespace QimErp.Shared.Common.Tests.Notifications;

public class IntentContractParserTests
{
    [Fact]
    public void PasswordResetContract_Parses()
    {
        var contract = IntentContractParser.Parse(File.ReadAllBytes(NotificationFixtures.IntentPath(NotificationFixtures.ResetKey, 1)));

        contract.Key.Should().Be(NotificationFixtures.ResetKey);
        contract.Version.Should().Be(1);
        contract.Audiences.Should().ContainSingle(a => a.Key == NotificationFixtures.ResetAudience && a.RecipientFrom == "userId");
        contract.References.Select(r => r.Name).Should().Equal("userId", "emailLinkLookupId");
        var actionUrl = contract.Fields.Single(f => f.Name == "ActionUrl");
        actionUrl.Type.Should().Be(IntentFieldType.Url);
        actionUrl.Sensitivity.Should().Be(DataClassification.Sensitive);
        actionUrl.ResolvedAt.Should().Be(IntentResolutionPhase.AfterPrecheck);
        actionUrl.Url.Should().Be(new IntentUrlRule("platform-frontend", "password_reset", "emailLinkLookupId", true));
        contract.Fields.Single(f => f.Name == "FirstName").Required.Should().BeFalse();
    }

    [Fact]
    public void ApprovalContract_Parses()
    {
        var contract = IntentContractParser.Parse(File.ReadAllBytes(NotificationFixtures.IntentPath(NotificationFixtures.ApprovalKey, 1)));

        contract.Key.Should().Be(NotificationFixtures.ApprovalKey);
        contract.Fields.Select(f => f.Name).Should().Contain(["ItemTitle", "ItemCode", "WorkspaceUrl", "ActionUrl"]);
        contract.Fields.Single(f => f.Name == "ActionUrl").Url.Should().Be(new IntentUrlRule("platform-frontend", "approval_review", "approvalRequestId", false));
        contract.Fields.Single(f => f.Name == "OccurredAt").Source.Should().Be(IntentFieldSources.Producer);
    }

    [Fact]
    public void UnknownTopLevelProperty_IsRejected()
    {
        var node = Contract();
        node["extra"] = true;

        var act = () => IntentContractParser.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void UnknownFieldProperty_IsRejected()
    {
        var node = Contract();
        node["fields"]![0]!["display"] = "x";

        var act = () => IntentContractParser.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void UnknownUrlRuleProperty_IsRejected()
    {
        var node = Contract();
        node["fields"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == "ActionUrl")!["url"]!["origin"] = "x";

        var act = () => IntentContractParser.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void DuplicateProperty_IsRejected()
    {
        var text = File.ReadAllText(NotificationFixtures.IntentPath(NotificationFixtures.ResetKey, 1));
        var duplicated = text.Replace("\"name\": \"FirstName\",", "\"name\": \"FirstName\",\n      \"name\": \"FirstName\",", StringComparison.Ordinal);
        duplicated.Should().NotBe(text);

        var act = () => IntentContractParser.Parse(Encoding.UTF8.GetBytes(duplicated));

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void UnsupportedSchemaVersion_IsRejected()
    {
        var node = Contract();
        node["schemaVersion"] = 2;

        var act = () => IntentContractParser.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));

        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData("value")]
    [InlineData("key")]
    public void LoneSurrogate_IsRejectedAsJsonException(string where)
    {
        var text = File.ReadAllText(NotificationFixtures.IntentPath(NotificationFixtures.ResetKey, 1));
        var mutated = where == "value"
            ? text.Replace("\"example\": \"Sample\"", "\"example\": \"\\ud800\"", StringComparison.Ordinal)
            : text.Replace("\"example\": \"Sample\"", "\"\\ud800\": \"Sample\"", StringComparison.Ordinal);
        mutated.Should().NotBe(text);

        var act = () => IntentContractParser.Parse(Encoding.UTF8.GetBytes(mutated));

        act.Should().Throw<JsonException>();
    }

    private static JsonObject Contract() =>
        JsonNode.Parse(File.ReadAllText(NotificationFixtures.IntentPath(NotificationFixtures.ResetKey, 1)))!.AsObject();
}

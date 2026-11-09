using System.Text;
using FluentAssertions;
using QimErp.Shared.Common.Services.Notifications.Registry;
using QimErp.Shared.Common.Services.Notifications.Rendering;
using Xunit;

namespace QimErp.Shared.Common.Tests.Notifications;

public class IntentTemplateRendererTests
{
    private readonly IntentTemplateRenderer _renderer = new(NotificationFixtures.AllowedSender);

    [Theory]
    [InlineData(NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, NotificationFixtures.ResetAudience, "full")]
    [InlineData(NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, NotificationFixtures.ResetAudience, "minimal")]
    [InlineData(NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, NotificationFixtures.ResetAudience, "precheck")]
    [InlineData(NotificationFixtures.ApprovalCode, NotificationFixtures.ApprovalKey, NotificationFixtures.ApprovalAudience, "full")]
    [InlineData(NotificationFixtures.ApprovalCode, NotificationFixtures.ApprovalKey, NotificationFixtures.ApprovalAudience, "minimal")]
    [InlineData(NotificationFixtures.ApprovalCode, NotificationFixtures.ApprovalKey, NotificationFixtures.ApprovalAudience, "precheck")]
    [InlineData(NotificationFixtures.OperatorCode, NotificationFixtures.OperatorKey, NotificationFixtures.OperatorAudience, "full")]
    [InlineData(NotificationFixtures.OperatorCode, NotificationFixtures.OperatorKey, NotificationFixtures.OperatorAudience, "minimal")]
    [InlineData(NotificationFixtures.OperatorCode, NotificationFixtures.OperatorKey, NotificationFixtures.OperatorAudience, "precheck")]
    public async Task Render_IsByteEqualToStage2Fixtures(string code, string key, string audience, string variant)
    {
        var pinned = await NotificationFixtures.PinnedAsync(code, key, audience);
        var values = NotificationFixtures.ExampleValues(pinned.Contract, key, requiredOnly: variant == "minimal");

        var result = variant == "precheck" ? _renderer.RenderPrecheck(pinned, values) : _renderer.RenderFinal(pinned, values);

        result.Failure.Should().Be(RenderFailure.None);
        var stem = $"{code}.{key}.v1.{audience}.{variant}";
        Encoding.UTF8.GetBytes(result.Email!.Html).Should().Equal(File.ReadAllBytes(NotificationFixtures.PreviewPath(stem + ".html")));
        Encoding.UTF8.GetBytes(result.Email.Text).Should().Equal(File.ReadAllBytes(NotificationFixtures.PreviewPath(stem + ".txt")));
        result.Email.Subject.Should().Be(values.Aggregate(pinned.Template.Subject, (subject, v) => subject.Replace("{{" + v.Key + "}}", v.Value, StringComparison.Ordinal)));
        result.Email.SenderAddress.Should().Be("no-reply@qimerp.com");
    }

    [Fact]
    public async Task Html_EscapesMarkupInValues_TextKeepsThemRaw()
    {
        var (pinned, values) = await ResetAsync();
        values["FirstName"] = "<script>&\"'";

        var result = _renderer.RenderFinal(pinned, values);

        result.Failure.Should().Be(RenderFailure.None);
        result.Email!.Html.Should().Contain("&lt;script&gt;&amp;&quot;&#x27;").And.NotContain("<script>");
        result.Email.Text.Should().Contain("<script>&\"'");
    }

    [Fact]
    public async Task Text_NormalisesLineBreaksInValues()
    {
        var (pinned, values) = await ResetAsync();
        values["FirstName"] = "Ama\r\nBcc: x\tKofi";

        var result = _renderer.RenderFinal(pinned, values);

        result.Failure.Should().Be(RenderFailure.None);
        result.Email!.Text.Should().Contain("Hi Ama  Bcc: x Kofi,").And.NotContain("\r");
        result.Email.Html.Should().NotContain("Ama\r\n");
    }

    [Fact]
    public async Task ValueContainingPlaceholder_IsNeverExpanded()
    {
        var (pinned, values) = await ApprovalAsync();
        values["FirstName"] = "{{ActionUrl}}";

        var result = _renderer.RenderFinal(pinned, values);

        result.Failure.Should().Be(RenderFailure.RenderResidue);
        result.Email.Should().BeNull();
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Fact]
    public async Task ValueContainingLatePlaceholder_FailsPrecheckByCount()
    {
        var (pinned, values) = await ResetAsync();
        values["FirstName"] = "{{ActionUrl}}";

        _renderer.RenderPrecheck(pinned, values).Failure.Should().Be(RenderFailure.RenderResidue);
    }

    [Fact]
    public async Task UnknownPlaceholder_IsRejected()
    {
        var (pinned, values) = await ResetAsync();
        var tampered = pinned with { Html = pinned.Html + "{{Bogus}}" };

        var result = _renderer.RenderFinal(tampered, values);

        result.Failure.Should().Be(RenderFailure.UnknownPlaceholder);
        result.FieldNames.Should().Equal("Bogus");
    }

    [Fact]
    public async Task GroupMemberWithWrongPhase_IsRejected()
    {
        var (pinned, values) = await ResetAsync();
        var groups = pinned.Declaration.Fields with { PlatformPrecheck = ["ActionUrl", "FirstName", "WorkspaceUrl"], PlatformAfterPrecheck = ["LinkExpiresAt"] };
        var tampered = pinned with { Declaration = pinned.Declaration with { Fields = groups } };

        var result = _renderer.RenderFinal(tampered, values);

        result.Failure.Should().Be(RenderFailure.UnknownPlaceholder);
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Fact]
    public async Task InfraTokens_AreUnsupported()
    {
        var (pinned, values) = await ResetAsync();
        var groups = pinned.Declaration.Fields with { Infra = ["Year"] };
        var tampered = pinned with { Declaration = pinned.Declaration with { Fields = groups } };

        _renderer.RenderFinal(tampered, values).Failure.Should().Be(RenderFailure.UnsupportedInfraToken);
    }

    [Fact]
    public async Task RequiredFieldMissing_ReportsNames_EvenWhenUnrendered()
    {
        var (pinned, values) = await ApprovalAsync();
        values.Remove("ItemTitle");
        values.Remove("RequesterName");

        var final = _renderer.RenderFinal(pinned, values);
        var precheck = _renderer.RenderPrecheck(pinned, values);

        pinned.Html.Should().NotContain("{{ItemTitle}}");
        final.Failure.Should().Be(RenderFailure.RequiredFieldMissing);
        final.FieldNames.Should().Equal("ItemTitle", "RequesterName");
        precheck.Failure.Should().Be(RenderFailure.RequiredFieldMissing);
        precheck.FieldNames.Should().Equal("ItemTitle", "RequesterName");
    }

    [Fact]
    public async Task AfterPrecheckFields_AreNotRequiredAtPrecheck()
    {
        var (pinned, values) = await ResetAsync();
        values.Remove("ActionUrl");
        values.Remove("LinkExpiresAt");

        _renderer.RenderPrecheck(pinned, values).Failure.Should().Be(RenderFailure.None);
        var final = _renderer.RenderFinal(pinned, values);
        final.Failure.Should().Be(RenderFailure.RequiredFieldMissing);
        final.FieldNames.Should().Equal("ActionUrl", "LinkExpiresAt");
    }

    [Fact]
    public async Task OptionalFieldWithoutValue_UsesFallback()
    {
        var (pinned, values) = await ApprovalAsync();
        values.Remove("FirstName");
        values.Remove("DueDate");

        var result = _renderer.RenderFinal(pinned, values);

        result.Failure.Should().Be(RenderFailure.None);
        result.Email!.Text.Should().Contain("Hi there,").And.Contain("Not set");
    }

    [Fact]
    public async Task OptionalFieldWithoutValueOrFallback_IsRequiredFieldMissing()
    {
        var (pinned, values) = await ResetAsync();
        values.Remove("FirstName");
        var tampered = pinned with { Declaration = pinned.Declaration with { Fallbacks = new Dictionary<string, string>() } };

        var result = _renderer.RenderFinal(tampered, values);

        result.Failure.Should().Be(RenderFailure.RequiredFieldMissing);
        result.FieldNames.Should().Equal("FirstName");
    }

    [Fact]
    public async Task PrecheckResidue_IsExactlyTheAfterPrecheckSet()
    {
        var (pinned, values) = await ResetAsync();

        var result = _renderer.RenderPrecheck(pinned, values);

        result.Failure.Should().Be(RenderFailure.None);
        Tokens(result.Email!.Html).Should().BeEquivalentTo(["ActionUrl", "LinkExpiresAt"]);
        Tokens(result.Email.Text).Should().BeEquivalentTo(["ActionUrl", "LinkExpiresAt"]);
        result.Email.Html.Should().NotContain(values["ActionUrl"]);
    }

    [Fact]
    public async Task OperatorPrecheck_LeavesNoResidue()
    {
        var pinned = await NotificationFixtures.PinnedAsync(NotificationFixtures.OperatorCode, NotificationFixtures.OperatorKey, NotificationFixtures.OperatorAudience);
        var values = NotificationFixtures.ExampleValues(pinned.Contract, NotificationFixtures.OperatorKey);

        var result = _renderer.RenderPrecheck(pinned, values);

        result.Failure.Should().Be(RenderFailure.None);
        Tokens(result.Email!.Html).Should().BeEmpty();
        Tokens(result.Email.Text).Should().BeEmpty();
    }

    [Fact]
    public async Task FinalRender_LeavesNoTokensOrMarkers()
    {
        var (pinned, values) = await ResetAsync();

        var result = _renderer.RenderFinal(pinned, values);

        result.Failure.Should().Be(RenderFailure.None);
        foreach (var part in new[] { result.Email!.Html, result.Email.Text, result.Email.Subject, result.Email.Preheader })
            IntentText.ContainsMarker(part).Should().BeFalse();
    }

    [Fact]
    public async Task StrayMarkerInTemplate_IsRenderResidue()
    {
        var (pinned, values) = await ResetAsync();
        var tampered = pinned with { Text = pinned.Text + "[[unresolved]]" };

        _renderer.RenderFinal(tampered, values).Failure.Should().Be(RenderFailure.RenderResidue);
    }

    [Fact]
    public async Task NonUrlInHref_IsContextViolation()
    {
        var (pinned, values) = await ResetAsync();
        var contexts = new Dictionary<string, IReadOnlyList<string>>(pinned.Declaration.PlaceholderContexts, StringComparer.Ordinal)
        {
            ["FirstName"] = ["htmlHref", "text"],
        };
        var tampered = pinned with { Declaration = pinned.Declaration with { PlaceholderContexts = contexts } };

        var result = _renderer.RenderFinal(tampered, values);

        result.Failure.Should().Be(RenderFailure.ContextViolation);
        result.FieldNames.Should().Equal("FirstName");
    }

    [Fact]
    public async Task UrlOutsideUrlContexts_IsContextViolation()
    {
        var (pinned, values) = await ResetAsync();
        var contexts = new Dictionary<string, IReadOnlyList<string>>(pinned.Declaration.PlaceholderContexts, StringComparer.Ordinal)
        {
            ["ActionUrl"] = ["htmlHref", "htmlText"],
        };
        var tampered = pinned with { Declaration = pinned.Declaration with { PlaceholderContexts = contexts } };

        _renderer.RenderFinal(tampered, values).Failure.Should().Be(RenderFailure.ContextViolation);
    }

    [Fact]
    public async Task RequiredLinkPresentInHtmlHrefVmlAndText_PassesPrecheck()
    {
        var (pinned, values) = await ResetPrecheckAsync();

        pinned.Html.Should().Contain(AnchorHref).And.Contain(VmlHref);
        pinned.Text.Should().Contain("{{ActionUrl}}");
        _renderer.RenderPrecheck(pinned, values).Failure.Should().Be(RenderFailure.None);
    }

    [Fact]
    public async Task RequiredLinkRemovedFromHtml_FailsPrecheck()
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var tampered = pinned with { Html = pinned.Html.Replace("{{ActionUrl}}", string.Empty, StringComparison.Ordinal) };

        var result = _renderer.RenderPrecheck(tampered, values);

        result.Failure.Should().Be(RenderFailure.RequiredLinkMissing);
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Fact]
    public async Task RequiredLinkRemovedFromText_FailsPrecheck()
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var tampered = pinned with { Text = pinned.Text.Replace("{{ActionUrl}}", string.Empty, StringComparison.Ordinal) };

        var result = _renderer.RenderPrecheck(tampered, values);

        result.Failure.Should().Be(RenderFailure.RequiredLinkMissing);
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Fact]
    public async Task RequiredPrecheckLinkRemovedFromHtml_FailsPrecheck()
    {
        var (pinned, values) = await ApprovalAsync();
        var tampered = pinned with { Html = pinned.Html.Replace("{{ActionUrl}}", "#", StringComparison.Ordinal) };

        var result = _renderer.RenderPrecheck(tampered, values);

        result.Failure.Should().Be(RenderFailure.RequiredLinkMissing);
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Theory]
    [InlineData("<p>{{ActionUrl}}</p>")]
    [InlineData("<p data-href=\"{{ActionUrl}}\">x</p>")]
    [InlineData("<link href=\"{{ActionUrl}}\">")]
    [InlineData("<!-- <a href=\"{{ActionUrl}}\">x</a> -->")]
    [InlineData("<!--[if !mso]><a href=\"{{ActionUrl}}\">x</a><![endif]-->")]
    [InlineData("<!--[if mso]><a href=\"{{ActionUrl}}\">x</a><![endif]-->")]
    [InlineData("<!--[if gte mso 9]><a href=\"{{ActionUrl}}\">x</a><![endif]-->")]
    [InlineData("<a title='x href=\"{{ActionUrl}}\"' href=\"#\">x</a>")]
    [InlineData("<a href=\"#\" href=\"{{ActionUrl}}\">x</a>")]
    [InlineData("<a href href=\"{{ActionUrl}}\">x</a>")]
    [InlineData("<a data-x=\"1\"href=\"#\" title=\"href={{ActionUrl}}\">x</a>")]
    public async Task RequiredLinkOnlyOutsideAnAnchorHref_FailsPrecheck(string usage)
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var html = pinned.Html.Replace(AnchorHref, "<a href=\"#\"", StringComparison.Ordinal) + usage;
        var tampered = pinned with { Html = html };

        var result = _renderer.RenderPrecheck(tampered, values);

        result.Failure.Should().Be(RenderFailure.RequiredLinkMissing);
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Theory]
    [InlineData("<!-- unterminated <a href=\"{{ActionUrl}}\">x</a>")]
    [InlineData("<a href=\"{{ActionUrl}}")]
    [InlineData("<a href=\"{{ActionUrl}}\"")]
    public async Task MalformedMarkup_FailsPrecheck(string suffix)
    {
        var (pinned, values) = await ResetPrecheckAsync();

        var result = _renderer.RenderPrecheck(pinned with { Html = pinned.Html + suffix }, values);

        result.Failure.Should().Be(RenderFailure.RequiredLinkMissing);
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Theory]
    [InlineData("href = \"{{ActionUrl}}\"")]
    [InlineData("HREF='{{ActionUrl}}'")]
    [InlineData("href={{ActionUrl}}")]
    public async Task AnchorHrefQuotingAndSpacingVariants_PassPrecheck(string href)
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var html = pinned.Html.Replace(AnchorHref, "<a " + href, StringComparison.Ordinal);
        html.Should().NotContain(AnchorHref);

        _renderer.RenderPrecheck(pinned with { Html = html }, values).Failure.Should().Be(RenderFailure.None);
    }

    [Theory]
    [InlineData("<a href=\"{{ActionUrl}}\" href=\"#\"")]
    [InlineData("<a title=\"a>b\" href=\"{{ActionUrl}}\"")]
    public async Task AnchorWhoseFirstRealHrefIsTheToken_PassesPrecheck(string anchor)
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var html = pinned.Html.Replace(AnchorHref, anchor, StringComparison.Ordinal);

        _renderer.RenderPrecheck(pinned with { Html = html }, values).Failure.Should().Be(RenderFailure.None);
    }

    [Fact]
    public async Task StrayCommentOpenerBeforeTheAnchor_FailsPrecheck()
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var html = pinned.Html.Replace(AnchorHref, "<!-- " + AnchorHref, StringComparison.Ordinal);

        _renderer.RenderPrecheck(pinned with { Html = html }, values).Failure.Should().Be(RenderFailure.RequiredLinkMissing);
    }

    [Fact]
    public async Task CrlfText_PassesPrecheck()
    {
        var (pinned, values) = await ResetPrecheckAsync();

        _renderer.RenderPrecheck(pinned with { Text = pinned.Text.Replace("\n", "\r\n", StringComparison.Ordinal) }, values)
            .Failure.Should().Be(RenderFailure.None);
    }

    [Fact]
    public async Task UnlabelledLinkInText_FailsPrecheck()
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var text = pinned.Text.Replace("Reset password: {{ActionUrl}}", "Reset password at {{ActionUrl}} now", StringComparison.Ordinal);
        text.Should().NotBe(pinned.Text);

        var result = _renderer.RenderPrecheck(pinned with { Text = text }, values);

        result.Failure.Should().Be(RenderFailure.RequiredLinkMissing);
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Fact]
    public async Task NotMsoConditionalComment_IsNotTheOutlookBlock()
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var html = pinned.Html.Replace("<!--[if mso]><v:roundrect", "<!--[if !mso]><v:roundrect", StringComparison.Ordinal)
            .Replace(VmlHref, "href=\"#\" style=", StringComparison.Ordinal);
        html.Should().NotBe(pinned.Html);

        _renderer.RenderPrecheck(pinned with { Html = html }, values).Failure.Should().Be(RenderFailure.None);
    }

    [Fact]
    public async Task AnyMsoConditionalComment_IsTheOutlookBlock()
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var html = pinned.Html.Replace("<!--[if mso]>", "<!--[if gte mso 9]>", StringComparison.Ordinal);

        _renderer.RenderPrecheck(pinned with { Html = html }, values).Failure.Should().Be(RenderFailure.None);
        var tampered = html.Replace(VmlHref, "href=\"#\" style=", StringComparison.Ordinal);
        _renderer.RenderPrecheck(pinned with { Html = tampered }, values).Failure.Should().Be(RenderFailure.RequiredLinkMissing);
    }

    [Fact]
    public async Task RequiredLinkMissingFromOutlookButton_FailsPrecheck()
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var tampered = pinned with { Html = pinned.Html.Replace(VmlHref, "href=\"#\" style=", StringComparison.Ordinal) };

        var result = _renderer.RenderPrecheck(tampered, values);

        result.Failure.Should().Be(RenderFailure.RequiredLinkMissing);
        result.FieldNames.Should().Equal("ActionUrl");
    }

    [Fact]
    public async Task TemplateWithoutOutlookButton_NeedsOnlyTheAnchorHref()
    {
        var (pinned, values) = await ResetPrecheckAsync();
        var html = System.Text.RegularExpressions.Regex.Replace(
            pinned.Html, @"<!--\[if mso\]>.*?<!\[endif\]-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        html.Should().NotContain(VmlHref).And.Contain(AnchorHref);

        _renderer.RenderPrecheck(pinned with { Html = html }, values).Failure.Should().Be(RenderFailure.None);
    }

    [Fact]
    public async Task PiiFieldInSubject_IsHeaderViolation()
    {
        var (pinned, values) = await ResetAsync();
        var tampered = pinned with { Template = pinned.Template with { Subject = "Reset for {{FirstName}}" } };

        var result = _renderer.RenderFinal(tampered, values);

        result.Failure.Should().Be(RenderFailure.HeaderViolation);
        result.FieldNames.Should().Equal("FirstName");
    }

    [Fact]
    public async Task DeclaredHtmlTitleContext_IsSubjectToHeaderRules()
    {
        var (pinned, values) = await ResetAsync();
        var contexts = new Dictionary<string, IReadOnlyList<string>>(pinned.Declaration.PlaceholderContexts, StringComparer.Ordinal)
        {
            ["WorkspaceUrl"] = ["htmlText", "htmlTitle", "text"],
        };
        var tampered = pinned with { Declaration = pinned.Declaration with { PlaceholderContexts = contexts } };

        _renderer.RenderFinal(tampered, values).Failure.Should().Be(RenderFailure.HeaderViolation);
    }

    [Theory]
    [InlineData("Visit www.example now")]
    [InlineData("Reset at example.com")]
    [InlineData("Ping https:x")]
    [InlineData("Reset\u200Bnow")]
    public async Task RawHostOrControlInRenderedHeader_IsHeaderViolation(string subject)
    {
        var (pinned, values) = await ResetAsync();
        var tampered = pinned with { Template = pinned.Template with { Subject = subject } };

        _renderer.RenderFinal(tampered, values).Failure.Should().Be(RenderFailure.HeaderViolation);
    }

    [Fact]
    public async Task WrongSender_IsSenderNotAllowed()
    {
        var (pinned, values) = await ResetAsync();
        var renderer = new IntentTemplateRenderer(new ManifestSender("QIM ERP", "alerts@qimerp.com"));

        renderer.RenderFinal(pinned, values).Failure.Should().Be(RenderFailure.SenderNotAllowed);
    }

    [Theory]
    [InlineData("Reset\U000E0041 now")]
    [InlineData("Reset\U000E0042")]
    [InlineData("Reset\U0001D173 now")]
    public async Task SupplementaryFormatCharacterInRenderedHeader_IsHeaderViolation(string subject)
    {
        var (pinned, values) = await ResetAsync();
        var tampered = pinned with { Template = pinned.Template with { Subject = subject } };

        _renderer.RenderFinal(tampered, values).Failure.Should().Be(RenderFailure.HeaderViolation);
    }

    [Fact]
    public async Task SupplementaryFormatCharactersInValues_AreStrippedFromHeadersAndBody()
    {
        var (pinned, values) = await ApprovalAsync();
        var tampered = pinned with { Template = pinned.Template with { Subject = "Approval needed at {{Stage}}" } };
        values["Stage"] = "2\U000E0041 of\U0001D173 3";
        values["FirstName"] = "Ama\U000E0042";

        var result = _renderer.RenderFinal(tampered, values);

        result.Failure.Should().Be(RenderFailure.None);
        result.Email!.Subject.Should().Be("Approval needed at 2 of 3");
        result.Email.Text.Should().Contain("Hi Ama,").And.NotContain("\U000E0042");
        result.Email.Html.Should().NotContain("\U000E0041").And.NotContain("\U0001D173");
    }

    [Fact]
    public async Task EmptyPreheader_RendersWithoutPreheader()
    {
        var (pinned, values) = await ResetAsync();
        var tampered = pinned with { Template = pinned.Template with { Preheader = "" } };

        var result = _renderer.RenderFinal(tampered, values);

        result.Failure.Should().Be(RenderFailure.None);
        result.Email!.Preheader.Should().BeEmpty();
    }

    [Fact]
    public async Task WhitespacePreheader_IsHeaderViolation()
    {
        var (pinned, values) = await ResetAsync();
        var tampered = pinned with { Template = pinned.Template with { Preheader = "  " } };

        _renderer.RenderFinal(tampered, values).Failure.Should().Be(RenderFailure.HeaderViolation);
    }

    [Fact]
    public async Task EmptySubject_IsStillHeaderViolation()
    {
        var (pinned, values) = await ResetAsync();
        var tampered = pinned with { Template = pinned.Template with { Subject = "" } };

        _renderer.RenderFinal(tampered, values).Failure.Should().Be(RenderFailure.HeaderViolation);
    }

    private static async Task<(PinnedTemplate Pinned, Dictionary<string, string> Values)> ResetAsync()
    {
        var pinned = await NotificationFixtures.PinnedAsync(NotificationFixtures.ResetCode, NotificationFixtures.ResetKey, NotificationFixtures.ResetAudience);
        return (pinned, new Dictionary<string, string>(NotificationFixtures.ExampleValues(pinned.Contract, NotificationFixtures.ResetKey), StringComparer.Ordinal));
    }

    private const string AnchorHref = "<a href=\"{{ActionUrl}}\"";
    private const string VmlHref = "href=\"{{ActionUrl}}\" style=";

    private static async Task<(PinnedTemplate Pinned, Dictionary<string, string> Values)> ResetPrecheckAsync()
    {
        var (pinned, values) = await ResetAsync();
        values.Remove("ActionUrl");
        values.Remove("LinkExpiresAt");
        return (pinned, values);
    }

    private static async Task<(PinnedTemplate Pinned, Dictionary<string, string> Values)> ApprovalAsync()
    {
        var pinned = await NotificationFixtures.PinnedAsync(NotificationFixtures.ApprovalCode, NotificationFixtures.ApprovalKey, NotificationFixtures.ApprovalAudience);
        return (pinned, new Dictionary<string, string>(NotificationFixtures.ExampleValues(pinned.Contract, NotificationFixtures.ApprovalKey), StringComparer.Ordinal));
    }

    private static IEnumerable<string> Tokens(string content) =>
        System.Text.RegularExpressions.Regex.Matches(content, @"\{\{([A-Z][A-Za-z0-9]*)\}\}").Select(m => m.Groups[1].Value).Distinct();
}

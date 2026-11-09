using FluentAssertions;
using QimErp.Shared.Common.Security;
using QimErp.Shared.Common.Services.Notifications.Rendering;
using Xunit;

namespace QimErp.Shared.Common.Tests.Notifications;

public class IntentValueValidatorTests
{
    private const string Origin = "https://app.qimerp.com";
    private const string Token = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCd";
    private const string ResetUrl = Origin + "/auth/reset-password?token=" + Token + "A";
    private const string ReviewUrl = Origin + "/baobab/workflows/review/0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b";

    private static readonly IntentField ResetLink = Field("ActionUrl", IntentFieldType.Url, new IntentUrlRule("platform-frontend", "password_reset", "emailLinkLookupId", true));
    private static readonly IntentField ReviewLink = Field("ActionUrl", IntentFieldType.Url, new IntentUrlRule("platform-frontend", "approval_review", "approvalRequestId", false));

    private const string ConsoleOrigin = "https://operator.qimerp.com";
    private const string ProfileUrl = ConsoleOrigin + "/tenants/0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b";

    private static readonly IntentField ProfileLink = Field("ActionUrl", IntentFieldType.Url, new IntentUrlRule("operator-console", "operator_tenant_profile", "subjectTenantId", false));

    private readonly IntentValueValidator _validator = new(Policy(Origin));

    [Fact]
    public void Datetime_RequiresUtcOffset()
    {
        var field = Field("OccurredAt", IntentFieldType.Datetime);

        _validator.Validate(field, new DateTimeOffset(2026, 11, 7, 9, 42, 0, TimeSpan.FromHours(1))).Failure.Should().Be(IntentValueFailure.InvalidFieldValue);
        _validator.Validate(field, "2026-11-07T09:42:00Z").Failure.Should().Be(IntentValueFailure.InvalidFieldValue);
        _validator.Validate(field, new DateTime(2026, 11, 7, 9, 42, 0, DateTimeKind.Utc)).Failure.Should().Be(IntentValueFailure.InvalidFieldValue);
    }

    [Theory]
    [InlineData(2026, 11, 7, 9, 42, "7 Nov 2026, 09:42 UTC")]
    [InlineData(2026, 1, 15, 17, 0, "15 Jan 2026, 17:00 UTC")]
    [InlineData(2027, 12, 31, 23, 59, "31 Dec 2027, 23:59 UTC")]
    public void Datetime_UsesFixtureFormat(int year, int month, int day, int hour, int minute, string expected)
    {
        var result = _validator.Validate(Field("OccurredAt", IntentFieldType.Datetime), new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));

        result.Ok.Should().BeTrue();
        result.DisplayValue.Should().Be(expected);
    }

    [Theory]
    [InlineData("workspace.example.qimerp.com", true)]
    [InlineData("baobab.qimerp.com", true)]
    [InlineData("a-b.co", true)]
    [InlineData("localhost", false)]
    [InlineData("-bad.qimerp.com", false)]
    [InlineData("bad-.qimerp.com", false)]
    [InlineData("a..qimerp.com", false)]
    [InlineData("qimerp.c", false)]
    [InlineData("exa mple.com", false)]
    [InlineData("https://qimerp.com", false)]
    [InlineData("qimerp.com\n", false)]
    [InlineData("qimerp.com/", false)]
    [InlineData("{{Host}}.com", false)]
    public void Hostname_Cases(string value, bool ok)
    {
        _validator.Validate(Field("WorkspaceUrl", IntentFieldType.Hostname), value).Ok.Should().Be(ok);
    }

    [Theory]
    [InlineData("http://app.qimerp.com/auth/reset-password?token=" + Token + "A")]
    [InlineData("https://user@app.qimerp.com/auth/reset-password?token=" + Token + "A")]
    [InlineData("https://user:pw@app.qimerp.com/auth/reset-password?token=" + Token + "A")]
    [InlineData("https://evil.qimerp.com/auth/reset-password?token=" + Token + "A")]
    [InlineData("https://app.qimerp.com.evil.com/auth/reset-password?token=" + Token + "A")]
    [InlineData("https://app.qimerp.com:8443/auth/reset-password?token=" + Token + "A")]
    [InlineData(ResetUrl + "&email=a@b.com")]
    [InlineData(ResetUrl + "#frag")]
    [InlineData(ResetUrl + "%0a")]
    [InlineData(ResetUrl + "%7F")]
    [InlineData("https://app.qimerp.com/auth/reset-password?token= " + Token)]
    [InlineData(ResetUrl + "\"")]
    [InlineData(ResetUrl + "'")]
    [InlineData("https://app.qimerp.com\\auth/reset-password?token=" + Token + "A")]
    [InlineData("https://app.qimerp.com/auth/../auth/reset-password?token=" + Token + "A")]
    [InlineData("https://APP.qimerp.com/auth/reset-password?token=" + Token + "A")]
    [InlineData("https://app.qimerp.com/auth/reset-password?token=short")]
    [InlineData("https://app.qimerp.com/workflow/entity/x/y/review")]
    public void Url_PolicyViolations_AreRejected(string url)
    {
        _validator.Validate(ResetLink, url).Failure.Should().Be(IntentValueFailure.UrlPolicyViolation);
    }

    [Fact]
    public void Url_WithControlCharacter_IsInvalid()
    {
        _validator.Validate(ResetLink, ResetUrl + "\n").Ok.Should().BeFalse();
    }

    [Fact]
    public void Url_WithoutPurposeRule_IsRejected()
    {
        var validator = new IntentValueValidator(new IntentUrlPolicy(
            new Dictionary<string, Uri> { ["platform-frontend"] = new(Origin) },
            new Dictionary<string, Func<Uri, bool>>()));

        validator.Validate(ResetLink, ResetUrl).Failure.Should().Be(IntentValueFailure.UrlPolicyViolation);
    }

    [Fact]
    public void Url_WithUnknownHostPolicy_IsRejected()
    {
        var field = Field("ActionUrl", IntentFieldType.Url, new IntentUrlRule("operator-console", "password_reset", null, false));

        _validator.Validate(field, ResetUrl).Failure.Should().Be(IntentValueFailure.UrlPolicyViolation);
    }

    [Fact]
    public void ValidResetAndApprovalUrls_Pass()
    {
        _validator.Validate(ResetLink, ResetUrl).Should().Be(IntentValueResult.Valid("ActionUrl", ResetUrl));
        _validator.Validate(ReviewLink, ReviewUrl).Should().Be(IntentValueResult.Valid("ActionUrl", ReviewUrl));
        _validator.Validate(ReviewLink, ResetUrl).Failure.Should().Be(IntentValueFailure.UrlPolicyViolation);
    }

    [Fact]
    public void OperatorTenantProfileUrl_PassesAtConfiguredOrigin()
    {
        _validator.Validate(ProfileLink, ProfileUrl).Should().Be(IntentValueResult.Valid("ActionUrl", ProfileUrl));
    }

    [Theory]
    [InlineData("https://other.qimerp.com/tenants/0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b")]
    [InlineData("https://operator.qimerp.com:8443/tenants/0190a3b4-5c6d-7e8f-9a0b-1c2d3e4f5a6b")]
    [InlineData(ProfileUrl + "?x=1")]
    [InlineData(ProfileUrl + "#frag")]
    public void OperatorTenantProfileUrl_RejectsOtherHostPortQueryOrFragment(string url)
    {
        _validator.Validate(ProfileLink, url).Failure.Should().Be(IntentValueFailure.UrlPolicyViolation);
    }

    [Fact]
    public void ConfiguredPort_MustMatch()
    {
        var validator = new IntentValueValidator(Policy("https://localhost:3000"));

        validator.Validate(ResetLink, "https://localhost:3000/auth/reset-password?token=" + Token + "A").Ok.Should().BeTrue();
        validator.Validate(ResetLink, "https://localhost/auth/reset-password?token=" + Token + "A").Failure.Should().Be(IntentValueFailure.UrlPolicyViolation);
        validator.Validate(ResetLink, "https://localhost:3001/auth/reset-password?token=" + Token + "A").Failure.Should().Be(IntentValueFailure.UrlPolicyViolation);
    }

    [Theory]
    [InlineData("Ama\r\nKofi", "Ama  Kofi")]
    [InlineData("Ama\tKofi", "Ama Kofi")]
    [InlineData("Ama\u200BKofi\u2028", "AmaKofi")]
    [InlineData("Ama\u0007", "Ama")]
    public void Text_IsNormalised(string raw, string expected)
    {
        _validator.Validate(Field("FirstName", IntentFieldType.Text), raw).DisplayValue.Should().Be(expected);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("{{ActionUrl}}")]
    [InlineData("a}}b")]
    [InlineData("[[x]]")]
    [InlineData("{\u200B{ActionUrl}}")]
    public void Text_BlankOrMarkers_AreInvalid(string raw)
    {
        _validator.Validate(Field("FirstName", IntentFieldType.Text), raw).Failure.Should().Be(IntentValueFailure.InvalidFieldValue);
    }

    [Fact]
    public void NonStringText_IsInvalid()
    {
        _validator.Validate(Field("FirstName", IntentFieldType.Text), 42).Failure.Should().Be(IntentValueFailure.InvalidFieldValue);
    }

    [Theory]
    [InlineData(IntentFieldType.Integer, "42", true)]
    [InlineData(IntentFieldType.Integer, "-7", true)]
    [InlineData(IntentFieldType.Integer, "4.2", false)]
    [InlineData(IntentFieldType.Integer, "\u0664\u0662", false)]
    [InlineData(IntentFieldType.Money, "GHS 120.50", true)]
    [InlineData(IntentFieldType.Money, "GHS -3", true)]
    [InlineData(IntentFieldType.Money, "ghs 1", false)]
    [InlineData(IntentFieldType.Money, "GHS 1.5", false)]
    [InlineData(IntentFieldType.Date, "2026-11-07", true)]
    [InlineData(IntentFieldType.Date, "2026-13-07", false)]
    [InlineData(IntentFieldType.Date, "07/11/2026", false)]
    public void ScalarTypes_FollowIntentsPy(IntentFieldType type, string value, bool ok)
    {
        _validator.Validate(Field("Value", type), value).Ok.Should().Be(ok);
    }

    [Fact]
    public void HtmlFragment_IsUnsupported()
    {
        _validator.Validate(Field("Body", IntentFieldType.HtmlFragment), "text").Failure.Should().Be(IntentValueFailure.UnsupportedFieldType);
    }

    [Theory]
    [InlineData("Ama\U000E0041Kofi", "AmaKofi")]
    [InlineData("\U000E0042Ama", "Ama")]
    [InlineData("Ama\U0001D173", "Ama")]
    public void Text_DropsSupplementaryFormatCharacters(string raw, string expected)
    {
        _validator.Validate(Field("FirstName", IntentFieldType.Text), raw).DisplayValue.Should().Be(expected);
    }

    [Fact]
    public void Text_DropsLoneSurrogates()
    {
        var raw = "Ama" + (char)0xD800 + "Kofi" + (char)0xDC00;

        _validator.Validate(Field("FirstName", IntentFieldType.Text), raw).DisplayValue.Should().Be("AmaKofi");
        IntentText.IsClean("Ama" + (char)0xD800).Should().BeFalse();
    }

    [Theory]
    [InlineData("\U000E0041")]
    [InlineData("\U0001D173")]
    public void Url_WithSupplementaryFormatCharacter_IsRejected(string format)
    {
        _validator.Validate(ResetLink, ResetUrl + format).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("/a%0Ab")]
    [InlineData("/a%0ab")]
    [InlineData("/a%1Fb")]
    [InlineData("/a%7fb")]
    [InlineData("/a?x=%0D")]
    public void EncodedControl_IsRejected_EvenWithPermissivePurposeRule(string suffix)
    {
        var validator = new IntentValueValidator(new IntentUrlPolicy(
            new Dictionary<string, Uri> { ["platform-frontend"] = new(Origin) },
            new Dictionary<string, Func<Uri, bool>> { ["password_reset"] = _ => true }));

        validator.Validate(ResetLink, Origin + "/ok").Ok.Should().BeTrue();
        validator.Validate(ResetLink, Origin + suffix).Failure.Should().Be(IntentValueFailure.UrlPolicyViolation);
    }

    [Theory]
    [InlineData("https://App.qimerp.com")]
    [InlineData("https://app.qimerp.com/")]
    [InlineData("https://app.qimerp.com:443")]
    [InlineData("https://app.qimerp.com/path")]
    [InlineData("http://app.qimerp.com")]
    [InlineData("https://user@app.qimerp.com")]
    public void Policy_RejectsNonCanonicalOrigins(string origin)
    {
        var act = () => Policy(origin);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("https://App.Qimerp.com:443/", "https://app.qimerp.com")]
    [InlineData("https://app.qimerp.com", "https://app.qimerp.com")]
    [InlineData("https://LOCALHOST:3000/", "https://localhost:3000")]
    public void NormalizeOrigin_ProducesAcceptedCanonicalOrigin(string raw, string expected)
    {
        var origin = IntentUrlPolicy.NormalizeOrigin(raw);

        origin.OriginalString.Should().Be(expected);
        var act = () => Policy(origin.OriginalString);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("https://app.qimerp.com/path")]
    [InlineData("https://app.qimerp.com/?q=1")]
    [InlineData("https://app.qimerp.com/#f")]
    [InlineData("http://app.qimerp.com")]
    [InlineData("https://u@app.qimerp.com")]
    [InlineData("not a url")]
    public void NormalizeOrigin_RejectsNonOrigins(string raw)
    {
        var act = () => IntentUrlPolicy.NormalizeOrigin(raw);

        act.Should().Throw<ArgumentException>();
    }

    private static IntentField Field(string name, IntentFieldType type, IntentUrlRule? url = null) =>
        new(name, type, true, IntentFieldSources.Platform, DataClassification.Internal, IntentResolutionPhase.Precheck, null, url);

    private static IntentUrlPolicy Policy(string origin) => new(
        new Dictionary<string, Uri> { ["platform-frontend"] = new(origin), ["operator-console"] = new(ConsoleOrigin) },
        new Dictionary<string, Func<Uri, bool>>
        {
            ["operator_tenant_profile"] = uri => System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/tenants/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")
                && uri.Query.Length == 0,
            ["password_reset"] = uri => uri.AbsolutePath == "/auth/reset-password"
                && System.Text.RegularExpressions.Regex.IsMatch(uri.Query, @"^\?token=[A-Za-z0-9_-]{43}\z"),
            ["approval_review"] = uri => System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/[a-z0-9-]+/workflows/review/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")
                && uri.Query.Length == 0,
        });
}

using System.Reflection;
using FluentAssertions;
using Moq;
using QimErp.Shared.Common.Activities.TenantActivity;
using QimErp.Shared.Common.Services.Auth;
using Xunit;

namespace QimErp.Shared.Common.Tests.TenantActivity;

public sealed class NewsImageRemovedActivityTests
{
    [Fact]
    public void News_image_removed_is_a_distinct_news_activity_type()
    {
        HrNewsActivityTypes.NewsImageRemoved.Should().Be("news-image-removed");

        var values = typeof(HrNewsActivityTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!)
            .ToList();
        values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void News_image_removed_records_against_the_news_item_by_the_current_user()
    {
        var actorId = Guid.NewGuid();
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.Setup(x => x.GetUserId()).Returns(actorId.ToString());
        var newsId = Guid.NewGuid();

        var request = TenantActivityRequestBuilder.ForNewsImageRemoved("tenant-a", newsId, "Year-end party", currentUser.Object);

        request.TenantId.Should().Be("tenant-a");
        request.Module.Should().Be(TenantActivityModules.Hr);
        request.ActivityType.Should().Be(HrNewsActivityTypes.NewsImageRemoved);
        request.Summary.Should().Be("The image was removed from news \"Year-end party\"");
        request.SubjectType.Should().Be("news");
        request.SubjectId.Should().Be(newsId);
        request.SubjectLabel.Should().Be("Year-end party");
        request.ActorUserId.Should().Be(actorId);
        request.CorrelationId.Should().StartWith($"{TenantActivityModules.Hr}:news-image-removed:{newsId:N}:");
        request.MetadataJson.Should().BeNull();
    }
}

using FluentAssertions;
using QimErp.Shared.Common.Activities.TenantReference;
using Temporalio.Api.Common.V1;
using Temporalio.Converters;
using Xunit;

namespace QimErp.Shared.Common.Tests.Activities;

public sealed class TenantReferenceSyncRequestHolidayTests
{
    private static readonly IPayloadConverter Converter = DataConverter.Default.PayloadConverter;

    private sealed class HolidayPayloadBeforeDescription
    {
        public string TenantId { get; set; } = string.Empty;
        public Guid EntityId { get; set; }
        public string? HolidayName { get; set; }
        public DateOnly? HolidayDate { get; set; }
        public string? HolidayType { get; set; }
        public string? HolidayCountryCode { get; set; }
        public string? HolidayRegion { get; set; }
        public bool IsRecurring { get; set; }
        public bool IsActive { get; set; }
    }

    [Fact]
    public void Holiday_description_survives_the_temporal_payload_round_trip()
    {
        var request = NewHolidayRequest();
        request.HolidayDescription = "Head office closes at noon.";

        var restored = RoundTrip<TenantReferenceSyncRequest>(Converter.ToPayload(request));

        restored.HolidayDescription.Should().Be("Head office closes at noon.");
        restored.HolidayType.Should().Be("Religious");
        restored.HolidayCountryCode.Should().Be("GH");
        restored.HolidayRegion.Should().Be("Volta");
        restored.IsActive.Should().BeTrue();
    }

    [Fact]
    public void A_payload_from_a_publisher_without_the_field_reads_a_null_description()
    {
        var legacy = new HolidayPayloadBeforeDescription
        {
            TenantId = "tenant-a",
            EntityId = Guid.NewGuid(),
            HolidayName = "Founders Day",
            HolidayDate = new DateOnly(2026, 8, 4),
            HolidayType = "Public"
        };

        var restored = RoundTrip<TenantReferenceSyncRequest>(Converter.ToPayload(legacy));

        restored.HolidayName.Should().Be("Founders Day");
        restored.HolidayDescription.Should().BeNull();
    }

    [Fact]
    public void A_consumer_built_before_the_field_still_reads_the_payload()
    {
        var request = NewHolidayRequest();
        request.HolidayDescription = "Head office closes at noon.";

        var restored = RoundTrip<HolidayPayloadBeforeDescription>(Converter.ToPayload(request));

        restored.HolidayName.Should().Be("Eid al-Fitr");
        restored.HolidayDate.Should().Be(new DateOnly(2026, 3, 20));
        restored.HolidayType.Should().Be("Religious");
    }

    private static TenantReferenceSyncRequest NewHolidayRequest() => new()
    {
        EntityType = TenantReferenceEntityType.Holiday,
        Operation = TenantReferenceSyncOperation.Created,
        TenantId = "tenant-a",
        EntityId = Guid.NewGuid(),
        HolidayName = "Eid al-Fitr",
        HolidayDate = new DateOnly(2026, 3, 20),
        HolidayType = "Religious",
        HolidayCountryCode = "GH",
        HolidayRegion = "Volta",
        IsActive = true
    };

    private static T RoundTrip<T>(Payload payload) => (T)Converter.ToValue(payload, typeof(T))!;
}

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using QimErp.Shared.Common.Entities.ValueObjects;
using Xunit;

namespace QimErp.Shared.Common.Tests.Entities;

public sealed class EntityReferenceTypeTests
{
    [Fact]
    public void Loan_applicant_is_appended_so_existing_ordinals_do_not_shift()
    {
        Enum.GetValues<EntityReferenceType>().Select(v => (int)v).Should().Equal(0, 1, 2, 3, 4, 5, 6);
        ((int)EntityReferenceType.Other).Should().Be(5);
        ((int)EntityReferenceType.LoanApplicant).Should().Be(6);
    }

    [Fact]
    public void Loan_applicant_round_trips_through_the_string_column_converter_and_json()
    {
        var converter = new EnumToStringConverter<EntityReferenceType>();
        converter.ConvertToProvider(EntityReferenceType.LoanApplicant).Should().Be("LoanApplicant");
        converter.ConvertFromProvider("LoanApplicant").Should().Be(EntityReferenceType.LoanApplicant);

        var reference = new EntityReference { ReferenceType = EntityReferenceType.LoanApplicant, ReferenceId = Guid.NewGuid() };
        JsonSerializer.Deserialize<EntityReference>(JsonSerializer.Serialize(reference))!.ReferenceType
            .Should().Be(EntityReferenceType.LoanApplicant);
        reference.HasValue.Should().BeTrue();
    }
}

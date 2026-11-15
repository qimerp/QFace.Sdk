using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QimErp.Shared.Common.Database.Configurations;
using QimErp.Shared.Common.Entities;
using Xunit;

namespace QimErp.Shared.Common.Tests.Database;

public sealed class AuditableEntityCustomFieldsTests
{
    public sealed class CustomFieldsThing : BaseAuditableEntity<Guid>
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CustomFieldsThingConfiguration : AuditableEntityConfiguration<CustomFieldsThing>;

    private sealed class CustomFieldsDbContext(DbContextOptions<CustomFieldsDbContext> options) : DbContext(options)
    {
        public DbSet<CustomFieldsThing> Things => Set<CustomFieldsThing>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyConfiguration(new CustomFieldsThingConfiguration());
    }

    private static CustomFieldsDbContext NewContext(string name) =>
        new(new DbContextOptionsBuilder<CustomFieldsDbContext>().UseInMemoryDatabase(name).Options);

    [Fact]
    public void Comparer_treats_dictionaries_with_same_entries_as_equal_in_any_order()
    {
        using var db = NewContext(Guid.NewGuid().ToString());
        var comparer = db.Model.FindEntityType(typeof(CustomFieldsThing))!
            .FindProperty(nameof(AuditableEntity.CustomFields))!.GetValueComparer();

        var a = new Dictionary<string, string> { ["x"] = "1", ["y"] = "2" };
        var b = new Dictionary<string, string> { ["y"] = "2", ["x"] = "1" };

        comparer.Equals(a, b).Should().BeTrue();
        comparer.GetHashCode(a).Should().Be(comparer.GetHashCode(b));
        comparer.Equals(a, new Dictionary<string, string> { ["x"] = "1" }).Should().BeFalse();
        comparer.Equals(null, null).Should().BeTrue();
        comparer.Equals(a, null).Should().BeFalse();
    }

    [Fact]
    public void In_place_change_to_custom_fields_is_saved()
    {
        var name = Guid.NewGuid().ToString();
        var id = Guid.NewGuid();
        using (var db = NewContext(name))
        {
            db.Things.Add(new CustomFieldsThing { Id = id, Name = "a", CustomFields = new() { ["colour"] = "red" } });
            db.SaveChanges();
        }

        using (var db = NewContext(name))
        {
            var thing = db.Things.Single(t => t.Id == id);
            thing.CustomFields!["colour"] = "blue";

            db.Entry(thing).Property(t => t.CustomFields).IsModified.Should().BeTrue();
            db.SaveChanges();
        }

        using (var db = NewContext(name))
        {
            db.Things.Single(t => t.Id == id).CustomFields.Should().ContainKey("colour").WhoseValue.Should().Be("blue");
        }
    }
}

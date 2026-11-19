using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace QimErp.Shared.Common.Database.Configurations;

public abstract class AuditableEntityConfiguration<TEntity> : IEntityTypeConfiguration<TEntity>
    where TEntity : AuditableEntity
{
    public virtual void Configure(EntityTypeBuilder<TEntity> builder)
    {
        builder.Property(e => e.Version)
            .IsRequired()
            .HasDefaultValue(1L)
            .IsConcurrencyToken();

        builder.Property(e => e.DataStatus).HasConversion(new EnumToStringConverter<DataState>());
        builder.Property(e => e.CustomFields)
            .HasColumnType("jsonb")
            .HasConversion(
                v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null),
                CustomFieldsComparer);
        builder.Property(e => e.PreviousDataStatus).HasConversion(new EnumToStringConverter<DataState>());
        
        builder.HasIndex(e => new { e.TenantId, e.CompanyId, e.DataStatus });
        builder.HasIndex(e => e.DataStatus);
        builder.HasIndex(e => e.Created);
        builder.HasIndex(e => e.LastModified);
    }

    private static readonly ValueComparer<Dictionary<string, string>?> CustomFieldsComparer = new(
        (a, b) => CustomFieldsEqual(a, b),
        d => CustomFieldsHash(d),
        d => d == null ? null : new Dictionary<string, string>(d));

    private static bool CustomFieldsEqual(Dictionary<string, string>? a, Dictionary<string, string>? b) =>
        a == null || b == null
            ? a == b
            : a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var value) && value == kv.Value);

    private static int CustomFieldsHash(Dictionary<string, string>? d) =>
        d == null ? 0 : d.Aggregate(0, (hash, kv) => hash ^ HashCode.Combine(kv.Key, kv.Value));
}


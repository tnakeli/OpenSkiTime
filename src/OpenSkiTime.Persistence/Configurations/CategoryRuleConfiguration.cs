using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OpenSkiTime.Domain.CategoryRules;

namespace OpenSkiTime.Persistence.Configurations;

internal sealed class CategoryRuleConfiguration : IEntityTypeConfiguration<CategoryRule>
{
    public void Configure(EntityTypeBuilder<CategoryRule> b)
    {
        b.ToTable("CategoryRules");

        b.HasKey(r => r.Id);
        b.Property(r => r.Id).ValueGeneratedNever();
        b.Property(r => r.EventSeriesId).IsRequired();
        b.Property(r => r.Label).HasMaxLength(100).IsRequired();
        b.Property(r => r.BirthYearMin).IsRequired();
        b.Property(r => r.BirthYearMax).IsRequired();
        b.Property(r => r.Gender).HasConversion<string>().HasMaxLength(10);
        b.Property(r => r.DisplayOrder).IsRequired();
    }
}

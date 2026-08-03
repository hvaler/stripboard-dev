using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class ElementConfiguration : IEntityTypeConfiguration<Element>
{
    public void Configure(EntityTypeBuilder<Element> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(250).IsRequired();
        builder.Property(e => e.Category).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(1000);
    }
}

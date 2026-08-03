using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class DisruptionConfiguration : IEntityTypeConfiguration<Disruption>
{
    public void Configure(EntityTypeBuilder<Disruption> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Timestamp).IsRequired();
        builder.Property(d => d.TriggerType).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(1000).IsRequired();
        builder.Property(d => d.ExpectedDurationDays).IsRequired();
    }
}

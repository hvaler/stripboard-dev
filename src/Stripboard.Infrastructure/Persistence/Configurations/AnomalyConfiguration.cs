using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class AnomalyConfiguration : IEntityTypeConfiguration<Anomaly>
{
    public void Configure(EntityTypeBuilder<Anomaly> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(a => a.Message).HasMaxLength(1000).IsRequired();
        builder.Property(a => a.Timestamp).IsRequired();

        builder.Property(a => a.SceneIds);
    }
}

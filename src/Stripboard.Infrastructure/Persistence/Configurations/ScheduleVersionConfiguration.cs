using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class ScheduleVersionConfiguration : IEntityTypeConfiguration<ScheduleVersion>
{
    public void Configure(EntityTypeBuilder<ScheduleVersion> builder)
    {
        builder.HasKey(sv => sv.Id);
        builder.Property(sv => sv.VersionNumber).IsRequired();
        builder.Property(sv => sv.CreatedAt).IsRequired();
        builder.Property(sv => sv.CreatedBy).HasMaxLength(150).IsRequired();
        builder.Property(sv => sv.IsCommitted).IsRequired();
    }
}

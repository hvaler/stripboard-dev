using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class StripConfiguration : IEntityTypeConfiguration<Strip>
{
    public void Configure(EntityTypeBuilder<Strip> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.SceneId).IsRequired();
        builder.Property(s => s.Order).IsRequired();
        builder.Property(s => s.EstimatedDurationMinutes).IsRequired();
    }
}

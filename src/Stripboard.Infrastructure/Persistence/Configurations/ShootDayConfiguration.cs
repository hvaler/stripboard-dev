using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class ShootDayConfiguration : IEntityTypeConfiguration<ShootDay>
{
    public void Configure(EntityTypeBuilder<ShootDay> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Date).IsRequired();
        builder.Property(s => s.DayNumber).IsRequired();
        builder.Property(s => s.LocationName).HasMaxLength(250).IsRequired();
        builder.Property(s => s.CallTime).IsRequired();
        builder.Property(s => s.EstimatedWrapTime).IsRequired();

        builder.Property(s => s.StripIds);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class SceneConfiguration : IEntityTypeConfiguration<Scene>
{
    public void Configure(EntityTypeBuilder<Scene> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Number).IsRequired();
        builder.Property(s => s.SetLocation).HasMaxLength(250).IsRequired();
        builder.Property(s => s.IntExt).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.DayNight).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(s => s.Eighths).IsRequired();
        builder.Property(s => s.Synopsis).HasMaxLength(2000);

        builder.Property(s => s.CastPersonIds)
            .HasColumnType("jsonb");

        builder.Property(s => s.ElementIds)
            .HasColumnType("jsonb");
    }
}

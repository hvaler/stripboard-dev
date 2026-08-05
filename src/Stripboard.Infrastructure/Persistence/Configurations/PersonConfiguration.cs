using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Name).HasMaxLength(250).IsRequired();
        builder.Property(p => p.Role).HasConversion<string>().HasMaxLength(50).IsRequired();
        builder.Property(p => p.DailyRate).HasPrecision(18, 2);
        builder.Property(p => p.MaxHoursPerDay).IsRequired();

        // Day Out of Days availability, stored alongside the person like the other
        // collection properties in this model (EV-27).
        builder.Property(p => p.UnavailableDates);
    }
}

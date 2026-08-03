using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stripboard.Domain.Entities;

namespace Stripboard.Infrastructure.Persistence.Configurations;

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(ae => ae.Id);
        builder.Property(ae => ae.Timestamp).IsRequired();
        builder.Property(ae => ae.EventType).HasMaxLength(100).IsRequired();
        builder.Property(ae => ae.Actor).HasMaxLength(150).IsRequired();
        builder.Property(ae => ae.Details).HasMaxLength(4000);
    }
}

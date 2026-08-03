using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Stripboard.Infrastructure.Persistence;
using Stripboard.Infrastructure.Persistence.Seeding;
using Xunit;

namespace Stripboard.Domain.Tests;

public class DbContextSeederTests
{
    [Fact]
    public async Task SeedAsync_ExecutesSeeding_IdempotentlyAndPopulatesAllEntities()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<StripboardDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var context = new StripboardDbContext(options);

        // Act - First Seeding
        await DataSeeder.SeedAsync(context);

        // Assert
        var scenesCount = await context.Scenes.CountAsync();
        var peopleCount = await context.People.CountAsync();
        var shootDaysCount = await context.ShootDays.CountAsync();
        var versionsCount = await context.ScheduleVersions.CountAsync();
        var auditEventsCount = await context.AuditEvents.CountAsync();

        scenesCount.Should().Be(12);
        peopleCount.Should().Be(6);
        shootDaysCount.Should().Be(3);
        versionsCount.Should().Be(1);
        auditEventsCount.Should().Be(1);

        // Act - Second Seeding (Idempotency Check)
        await DataSeeder.SeedAsync(context);

        // Assert: Counts should remain identical
        (await context.Scenes.CountAsync()).Should().Be(12);
        (await context.ScheduleVersions.CountAsync()).Should().Be(1);
    }
}

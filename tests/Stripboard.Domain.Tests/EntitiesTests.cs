using FluentAssertions;
using Stripboard.Domain.Entities;
using Stripboard.Domain.Enums;
using Xunit;

namespace Stripboard.Domain.Tests;

public class EntitiesTests
{
    [Fact]
    public void Scene_Creation_ValidParameters_SetsPropertiesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();

        // Act
        var scene = new Scene(
            id: id,
            number: 1,
            setLocation: "MANOR HOUSE",
            intExt: IntExt.Int,
            dayNight: DayNight.Night,
            eighths: 4,
            synopsis: "Lord Blackwood receives the unexpected letter."
        );

        // Assert
        scene.Id.Should().Be(id);
        scene.Number.Should().Be(1);
        scene.SetLocation.Should().Be("MANOR HOUSE");
        scene.IntExt.Should().Be(IntExt.Int);
        scene.DayNight.Should().Be(DayNight.Night);
        scene.Eighths.Should().Be(4);
        scene.Synopsis.Should().Be("Lord Blackwood receives the unexpected letter.");
        scene.CastPersonIds.Should().BeEmpty();
    }

    [Fact]
    public void Scene_Creation_InvalidNumber_ThrowsArgumentOutOfRangeException()
    {
        // Act
        Action act = () => new Scene(Guid.NewGuid(), number: 0, "SET", IntExt.Int, DayNight.Day, eighths: 1);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Person_IsCast_ShouldReturnTrueForCastRoleOnly()
    {
        // Arrange & Act
        var castPerson = new Person(Guid.NewGuid(), "Actor", PersonRole.Cast);
        var crewPerson = new Person(Guid.NewGuid(), "Operator", PersonRole.Crew);

        // Assert
        castPerson.IsCast.Should().BeTrue();
        crewPerson.IsCast.Should().BeFalse();
    }

    [Fact]
    public void ShootDay_GetWrapDateTime_OvernightWrap_CalculatesCorrectNextDayWrap()
    {
        // Arrange
        var date = new DateOnly(2026, 8, 10);
        var callTime = new TimeOnly(20, 0); // 8:00 PM
        var wrapTime = new TimeOnly(4, 0);   // 4:00 AM (next morning)

        // Act
        var shootDay = new ShootDay(Guid.NewGuid(), date, dayNumber: 1, "LOCATION", callTime, wrapTime);

        // Assert
        shootDay.GetCallDateTime().Should().Be(new DateTime(2026, 8, 10, 20, 0, 0));
        shootDay.GetWrapDateTime().Should().Be(new DateTime(2026, 8, 11, 4, 0, 0));
    }
}

using FluentAssertions;
using Stripboard.CallSheets.Services;
using Xunit;

namespace Stripboard.CallSheets.Tests;

public class CallSheetGeneratorTests
{
    [Fact]
    public void CallSheetPdfGenerator_GeneratesValidPdfBytes_Successfully()
    {
        // Arrange
        var generator = new CallSheetPdfGenerator();
        var data = new CallSheetData(
            ProductionTitle: "TEST MOVIE",
            ShootDate: new DateOnly(2026, 8, 10),
            DayNumber: 1,
            TotalDays: 10,
            PersonName: "Sherlock Holmes",
            RoleTitle: "Lead Cast",
            IndividualCallTime: "06:00 AM",
            SunriseTime: "05:42 AM",
            SunsetTime: "08:35 PM",
            WeatherSummary: "Sunny 22°C",
            PrimaryLocation: "221B BAKER STREET",
            Scenes: new List<CallSheetSceneItem>
            {
                new(1, "221B BAKER STREET", "INT", "DAY", "Sherlock Holmes", "Cast #1")
            }
        );

        // Act
        byte[] pdf = generator.GenerateCallSheetPdf(data);

        // Assert
        pdf.Should().NotBeNull();
        pdf.Length.Should().BeGreaterThan(1000);
        // PDF Magic Header %PDF-
        pdf[0].Should().Be(0x25); // '%'
        pdf[1].Should().Be(0x50); // 'P'
        pdf[2].Should().Be(0x44); // 'D'
        pdf[3].Should().Be(0x46); // 'F'
    }
}

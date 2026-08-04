using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Stripboard.CallSheets.Services;

public record CallSheetSceneItem(
    int SceneNumber,
    string SetLocation,
    string IntExt,
    string DayNight,
    string Description,
    string CastIds
);

public record CallSheetData(
    string ProductionTitle,
    DateOnly ShootDate,
    int DayNumber,
    int TotalDays,
    string PersonName,
    string RoleTitle,
    string IndividualCallTime,
    string SunriseTime,
    string SunsetTime,
    string WeatherSummary,
    string PrimaryLocation,
    List<CallSheetSceneItem> Scenes
);

public class CallSheetPdfGenerator
{
    static CallSheetPdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GenerateCallSheetPdf(CallSheetData data)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10).FontFamily("Helvetica"));

                page.Header().Element(header => BuildHeader(header, data));
                page.Content().Element(content => BuildContent(content, data));
                page.Footer().Element(footer => BuildFooter(footer));
            });
        });

        return document.GeneratePdf();
    }

    private static void BuildHeader(IContainer container, CallSheetData data)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(data.ProductionTitle.ToUpper()).FontSize(20).Bold().FontColor(Colors.Blue.Darken2);
                col.Item().Text($"DAY {data.DayNumber} OF {data.TotalDays} — CALL SHEET").FontSize(12).SemiBold().FontColor(Colors.Grey.Darken2);
            });

            row.ConstantItem(150).Column(col =>
            {
                col.Item().Text($"DATE: {data.ShootDate:yyyy-MM-dd}").Bold();
                col.Item().Text($"SUNRISE: {data.SunriseTime}");
                col.Item().Text($"SUNSET: {data.SunsetTime}");
            });
        });
    }

    private static void BuildContent(IContainer container, CallSheetData data)
    {
        container.PaddingVertical(1, Unit.Centimetre).Column(col =>
        {
            // Call Time & Personal Details Box
            col.Item().Background(Colors.Grey.Lighten3).Padding(10).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"NAME: {data.PersonName}").Bold().FontSize(12);
                    c.Item().Text($"ROLE / TITLE: {data.RoleTitle}");
                    c.Item().Text($"LOCATION: {data.PrimaryLocation}");
                });
                row.ConstantItem(150).Column(c =>
                {
                    c.Item().Text("INDIVIDUAL CALL TIME").FontSize(9).SemiBold();
                    c.Item().Text(data.IndividualCallTime).Bold().FontSize(18).FontColor(Colors.Red.Medium);
                });
            });

            col.Item().PaddingVertical(10).Text($"WEATHER FORECAST: {data.WeatherSummary}").Italic().FontSize(10);

            // Scenes Table
            col.Item().Text("SCHEDULED SCENES").Bold().FontSize(12).FontColor(Colors.Blue.Darken2);
            col.Item().PaddingTop(5).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(40);
                    columns.RelativeColumn(3);
                    columns.ConstantColumn(60);
                    columns.ConstantColumn(60);
                    columns.RelativeColumn(2);
                });

                table.Header(header =>
                {
                    header.Cell().Background(Colors.Blue.Darken2).Padding(4).Text("SC#").Bold().FontColor(Colors.White);
                    header.Cell().Background(Colors.Blue.Darken2).Padding(4).Text("LOCATION / SET").Bold().FontColor(Colors.White);
                    header.Cell().Background(Colors.Blue.Darken2).Padding(4).Text("INT/EXT").Bold().FontColor(Colors.White);
                    header.Cell().Background(Colors.Blue.Darken2).Padding(4).Text("D/N").Bold().FontColor(Colors.White);
                    header.Cell().Background(Colors.Blue.Darken2).Padding(4).Text("CAST").Bold().FontColor(Colors.White);
                });

                foreach (var scene in data.Scenes)
                {
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(scene.SceneNumber.ToString());
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(scene.SetLocation);
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(scene.IntExt);
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(scene.DayNight);
                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Text(scene.CastIds);
                }
            });
        });
    }

    private static void BuildFooter(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Text("CONFIDENTIAL — FOR CAST & CREW USE ONLY").FontSize(8).Italic().FontColor(Colors.Grey.Medium);
            row.RelativeItem().AlignRight().Text("Stripboard Autonomous Line Producer").FontSize(8).FontColor(Colors.Grey.Medium);
        });
    }
}

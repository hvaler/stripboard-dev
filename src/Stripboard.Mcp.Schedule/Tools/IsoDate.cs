using System.Globalization;
using ModelContextProtocol;

namespace Stripboard.Mcp.Schedule.Tools;

/// <summary>
/// Parses a date the way the tool descriptions promise: ISO, and only ISO.
///
/// <c>DateOnly.TryParse</c> uses the current culture, so <c>10/08/2026</c> is the 10th of
/// August on a Spanish machine and the 8th of October on an American one — the same argument
/// silently producing two different shooting days depending on where the server happens to
/// run. It also accepts things our schema never advertised, which teaches a model that the
/// documented format is optional.
///
/// This is the third time a locale default has bitten this project: the compiler's ANSI
/// codepage fallback (ADR-017) and requests' ISO-8859-1 body decoding (ADR-019) were the same
/// mistake in different clothes — a default that guesses, and produces something wrong rather
/// than something absent.
/// </summary>
internal static class IsoDate
{
    public static DateOnly Parse(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : throw new McpException(
                $"'{value}' is not a date in ISO format. Use YYYY-MM-DD, e.g. 2026-08-10.");
}

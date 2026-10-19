using System.Globalization;
using System.Text;

namespace DedupTool;

/// <summary>
/// Writes the load file consumed by the review platform ingest job.
/// Column order is fixed. Do not change without coordinating with the ingest team.
/// </summary>
public static class LoadFileWriter
{
    public const string Header = "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash";

    private static readonly char[] CsvSpecialCharacters = [',', '"', '\r', '\n'];

    public static string ToCsv(IEnumerable<LoadFileRow> rows)
    {
        var builder = new StringBuilder();
        using var writer = new StringWriter(builder, CultureInfo.InvariantCulture);
        WriteRows(writer, rows);
        return builder.ToString();
    }

    public static void Write(string path, IEnumerable<LoadFileRow> rows)
    {
        using var writer = new StreamWriter(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 65_536);
        WriteRows(writer, rows);
    }

    private static void WriteRows(TextWriter writer, IEnumerable<LoadFileRow> rows)
    {
        // Ingest expects UTF-8 without a BOM and LF line endings on every platform.
        writer.Write(Header);
        writer.Write('\n');

        foreach (var row in rows)
        {
            WriteField(writer, row.DocId);
            writer.Write(',');
            WriteField(writer, row.FamilyId);
            writer.Write(',');
            WriteField(writer, row.ParentId);
            writer.Write(',');
            WriteField(writer, row.Custodian);
            writer.Write(',');
            WriteField(writer, row.DocType);
            writer.Write(',');
            WriteField(writer, row.FileName);
            writer.Write(',');
            WriteField(writer, row.SentDate);
            writer.Write(',');
            WriteField(writer, row.Hash);
            writer.Write('\n');
        }
    }

    private static void WriteField(TextWriter writer, string value)
    {
        if (value.IndexOfAny(CsvSpecialCharacters) < 0)
        {
            writer.Write(value);
            return;
        }

        writer.Write('"');
        writer.Write(value.Replace("\"", "\"\"", StringComparison.Ordinal));
        writer.Write('"');
    }
}

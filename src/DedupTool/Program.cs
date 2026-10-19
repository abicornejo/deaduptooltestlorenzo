using System.Text.Json;
using DedupTool;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: DedupTool <batch.json> <loadfile.csv>");
            return 2;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var input = File.OpenRead(args[0]);
        var batch = JsonSerializer.Deserialize<Batch>(input, options)
                    ?? throw new InvalidOperationException("Batch file was empty.");

        var result = Deduper.Run(batch);
        LoadFileWriter.Write(args[1], result.Rows);

        Console.WriteLine($"{batch.BatchId}: {batch.Documents.Count} documents in, {result.Rows.Count} out, {result.Suppressed} suppressed as duplicates.");
        return 0;
    }
}

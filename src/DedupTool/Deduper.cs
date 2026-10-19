using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DedupTool;

/// <summary>
/// Suppresses duplicate content while preserving complete email families.
/// </summary>
public static class Deduper
{
    private static readonly Regex SentDatePattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant);

    public static DedupResult Run(Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var byId = IndexDocuments(batch.Documents);
        var familyInfoById = ResolveFamilies(batch.Documents, byId);
        var retainedFamilyIds = SelectRetainedFamilies(batch.Documents, familyInfoById);

        var rows = batch.Documents
            .Where(document => retainedFamilyIds.Contains(familyInfoById[document.DocId].Root.DocId))
            .OrderBy(document => familyInfoById[document.DocId].Root.DocId, StringComparer.Ordinal)
            .ThenBy(document => familyInfoById[document.DocId].Depth)
            .ThenBy(document => document.DocId, StringComparer.Ordinal)
            .Select(document => ToLoadFileRow(document, familyInfoById[document.DocId].Root))
            .ToList();

        return new DedupResult(rows, batch.Documents.Count - rows.Count);
    }

    private static Dictionary<string, Document> IndexDocuments(IEnumerable<Document> documents)
    {
        var byId = new Dictionary<string, Document>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            ValidateDocumentShape(document);

            if (!byId.TryAdd(document.DocId, document))
            {
                throw new InvalidOperationException($"Duplicate docId '{document.DocId}'.");
            }
        }

        return byId;
    }

    private static void ValidateDocumentShape(Document document)
    {
        RequireValue(document.DocId, document.DocId, "docId");
        RequireValue(document.Custodian, document.DocId, "custodian");
        RequireValue(document.FileName, document.DocId, "fileName");
        RequireValue(document.Sha256, document.DocId, "sha256");

        switch (document.DocType)
        {
            case "email" when document.ParentId is not null:
                throw new InvalidOperationException($"Email '{document.DocId}' cannot have a parent.");
            case "attachment" when document.ParentId is null:
                throw new InvalidOperationException($"Attachment '{document.DocId}' must have a parent.");
            case "loose" when document.ParentId is not null:
                throw new InvalidOperationException($"Loose file '{document.DocId}' cannot have a parent.");
            case "email":
            case "attachment":
            case "loose":
                return;
            default:
                throw new InvalidOperationException(
                    $"Document '{document.DocId}' has unsupported docType '{document.DocType}'.");
        }
    }

    private static void RequireValue(string? value, string documentId, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Document '{documentId}' has an invalid or missing '{fieldName}'.");
        }
    }

    private static Dictionary<string, FamilyInfo> ResolveFamilies(
        IEnumerable<Document> documents,
        IReadOnlyDictionary<string, Document> byId)
    {
        var resolved = new Dictionary<string, FamilyInfo>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            ResolveFamily(document, byId, resolved);
        }

        foreach (var document in documents)
        {
            var root = resolved[document.DocId].Root;
            if (document.DocType == "attachment" && root.DocType != "email")
            {
                throw new InvalidOperationException(
                    $"Attachment '{document.DocId}' must belong to a family rooted in an email, not '{root.DocType}'.");
            }
        }

        return resolved;
    }

    private static FamilyInfo ResolveFamily(
        Document document,
        IReadOnlyDictionary<string, Document> byId,
        IDictionary<string, FamilyInfo> resolved)
    {
        if (resolved.TryGetValue(document.DocId, out var existing))
        {
            return existing;
        }

        var path = new List<Document>();
        var pathIds = new HashSet<string>(StringComparer.Ordinal);
        var current = document;
        FamilyInfo? anchor = null;

        while (true)
        {
            if (resolved.TryGetValue(current.DocId, out existing))
            {
                anchor = existing;
                break;
            }

            if (!pathIds.Add(current.DocId))
            {
                throw new InvalidOperationException($"Parent cycle detected at document '{current.DocId}'.");
            }

            path.Add(current);
            if (current.ParentId is null)
            {
                break;
            }

            if (!byId.TryGetValue(current.ParentId, out var parent))
            {
                throw new InvalidOperationException(
                    $"Parent '{current.ParentId}' of document '{current.DocId}' was not found.");
            }

            current = parent;
        }

        for (var index = path.Count - 1; index >= 0; index--)
        {
            var node = path[index];
            var info = node.ParentId is null
                ? new FamilyInfo(node, 0)
                : new FamilyInfo(anchor!.Root, anchor.Depth + 1);

            resolved[node.DocId] = info;
            anchor = info;
        }

        return resolved[document.DocId];
    }

    private static HashSet<string> SelectRetainedFamilies(
        IEnumerable<Document> documents,
        IReadOnlyDictionary<string, FamilyInfo> familyInfoById)
    {
        var retained = new HashSet<string>(StringComparer.Ordinal);
        var looseHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emailKeys = new HashSet<EmailDedupKey>();

        foreach (var root in documents
                     .Where(document => familyInfoById[document.DocId].Depth == 0)
                     .OrderBy(document => document.DocId, StringComparer.Ordinal))
        {
            var keep = root.DocType switch
            {
                "loose" => looseHashes.Add(root.Sha256),
                "email" => emailKeys.Add(CreateEmailDedupKey(root)),
                _ => throw new InvalidOperationException(
                    $"Family '{root.DocId}' has invalid top-level docType '{root.DocType}'."),
            };

            if (keep)
            {
                retained.Add(root.DocId);
            }
        }

        return retained;
    }

    private static EmailDedupKey CreateEmailDedupKey(Document email)
    {
        var sentDate = ParseSentDate(email);
        var sender = NormalizeAddress(RequiredEmailField(email.From, email, "from"));
        var recipients = email.To
            ?? throw new InvalidOperationException($"Email '{email.DocId}' is missing 'to'.");
        var normalizedRecipients = string.Join(
            '\u001f',
            recipients
                .Select(NormalizeAddress)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(recipient => recipient, StringComparer.Ordinal));
        var subject = NormalizeText(RequiredEmailField(email.Subject, email, "subject"));
        var body = NormalizeLineEndings(RequiredEmailField(email.Body, email, "body"));

        return new EmailDedupKey(
            sentDate.UtcDateTime.Ticks,
            sender,
            normalizedRecipients,
            subject,
            body);
    }

    private static string RequiredEmailField(string? value, Document email, string fieldName) =>
        value ?? throw new InvalidOperationException($"Email '{email.DocId}' is missing '{fieldName}'.");

    private static string NormalizeAddress(string value) =>
        NormalizeText(value.Trim()).ToLowerInvariant();

    private static string NormalizeText(string value) => value.Normalize(NormalizationForm.FormC);

    private static string NormalizeLineEndings(string value) =>
        NormalizeText(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));

    private static DateTimeOffset ParseSentDate(Document email)
    {
        if (email.SentDate is null
            || !SentDatePattern.IsMatch(email.SentDate)
            || !DateTimeOffset.TryParse(
                email.SentDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var sentDate))
        {
            throw new InvalidOperationException(
                $"Email '{email.DocId}' has an invalid or missing sentDate '{email.SentDate}'.");
        }

        return sentDate;
    }

    private static LoadFileRow ToLoadFileRow(Document document, Document root) => new()
    {
        DocId = document.DocId,
        FamilyId = root.DocId,
        ParentId = document.ParentId ?? "",
        Custodian = document.Custodian,
        DocType = document.DocType,
        FileName = document.FileName,
        SentDate = root.DocType == "email"
            ? ParseSentDate(root).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : "",
        Hash = document.Sha256,
    };

    private sealed record FamilyInfo(Document Root, int Depth);

    private readonly record struct EmailDedupKey(
        long SentDateUtcTicks,
        string From,
        string To,
        string Subject,
        string Body);
}

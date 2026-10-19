using System.Text.Json;
using DedupTool;
using Xunit;

namespace DedupTool.Tests;

public class DeduperTests
{
    private static Document Loose(string id, string custodian, string hash) => new()
    {
        DocId = id,
        Custodian = custodian,
        DocType = "loose",
        FileName = id + ".txt",
        Sha256 = hash,
        SizeBytes = 10,
    };

    private static Document Email(
        string id,
        string custodian,
        string sentDate,
        string from = "sender@example.com",
        string[]? to = null,
        string subject = "Subject",
        string body = "Body") => new()
    {
        DocId = id,
        Custodian = custodian,
        DocType = "email",
        FileName = id + ".eml",
        Sha256 = "email-" + id,
        SizeBytes = 100,
        SentDate = sentDate,
        From = from,
        To = to?.ToList() ?? ["recipient@example.com"],
        Subject = subject,
        Body = body,
    };

    private static Document Attachment(string id, string parentId, string custodian, string hash) => new()
    {
        DocId = id,
        ParentId = parentId,
        Custodian = custodian,
        DocType = "attachment",
        FileName = id + ".bin",
        Sha256 = hash,
        SizeBytes = 10,
    };

    [Fact]
    public void IdenticalLooseDocuments_LowestDocIdIsRetained()
    {
        var batch = new Batch
        {
            Documents = [Loose("B", "c2", "h1"), Loose("A", "c1", "h1")],
        };

        var result = Deduper.Run(batch);

        Assert.Single(result.Rows);
        Assert.Equal("A", result.Rows[0].DocId);
        Assert.Equal(1, result.Suppressed);
    }

    [Fact]
    public void DistinctDocuments_AllKept()
    {
        var batch = new Batch
        {
            Documents = [Loose("A", "c1", "h1"), Loose("B", "c1", "h2")],
        };

        var result = Deduper.Run(batch);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(0, result.Suppressed);
    }

    [Fact]
    public void DistinctEmailsWithSameAttachment_KeepBothFamiliesIntact()
    {
        var batch = new Batch
        {
            Documents =
            [
                Email("E1", "c1", "2024-03-05T14:22:00-05:00", subject: "First"),
                Attachment("A1", "E1", "c1", "shared-hash"),
                Email("E2", "c1", "2024-03-06T14:22:00-05:00", subject: "Second"),
                Attachment("A2", "E2", "c1", "shared-hash"),
            ],
        };

        var result = Deduper.Run(batch);

        Assert.Equal(["E1", "A1", "E2", "A2"], result.Rows.Select(row => row.DocId));
        Assert.Equal(0, result.Suppressed);
        Assert.Equal("E1", result.Rows.Single(row => row.DocId == "A1").FamilyId);
        Assert.Equal("E2", result.Rows.Single(row => row.DocId == "A2").FamilyId);
    }

    [Fact]
    public void SameEmailAcrossMailboxes_SuppressesDuplicateFamily()
    {
        var batch = new Batch
        {
            Documents =
            [
                Email(
                    "E2",
                    "recipient",
                    "2024-03-05T19:22:00Z",
                    from: "SENDER@example.com",
                    to: ["SECOND@example.com", "recipient@example.com"],
                    body: "Line one\nLine two"),
                Attachment("A2", "E2", "recipient", "recipient-copy"),
                Email(
                    "E1",
                    "sender",
                    "2024-03-05T14:22:00-05:00",
                    from: "sender@example.com",
                    to: ["recipient@example.com", "second@example.com"],
                    body: "Line one\r\nLine two"),
                Attachment("A1", "E1", "sender", "sender-copy"),
            ],
        };

        var result = Deduper.Run(batch);

        Assert.Equal(["E1", "A1"], result.Rows.Select(row => row.DocId));
        Assert.Equal(2, result.Suppressed);
    }

    [Fact]
    public void NestedAttachments_UseTopLevelFamilyAndUtcSentDate()
    {
        var batch = new Batch
        {
            Documents =
            [
                Attachment("A2", "A1", "c1", "h2"),
                Attachment("A1", "E1", "c1", "h1"),
                Email("E1", "c1", "2024-03-05T14:22:00.987-05:00"),
            ],
        };

        var result = Deduper.Run(batch);
        var grandchild = result.Rows.Single(row => row.DocId == "A2");

        Assert.Equal(["E1", "A1", "A2"], result.Rows.Select(row => row.DocId));
        Assert.Equal("E1", grandchild.FamilyId);
        Assert.Equal("A1", grandchild.ParentId);
        Assert.Equal("2024-03-05T19:22:00Z", grandchild.SentDate);
    }

    [Fact]
    public void MissingParent_FailsWithDocumentContext()
    {
        var batch = new Batch
        {
            Documents = [Attachment("A1", "missing", "c1", "h1")],
        };

        var error = Assert.Throws<InvalidOperationException>(() => Deduper.Run(batch));

        Assert.Contains("Parent 'missing'", error.Message);
        Assert.Contains("document 'A1'", error.Message);
    }

    [Fact]
    public void MissingRequiredHash_FailsBeforeDeduplication()
    {
        var document = Loose("A", "c1", "hash");
        document.Sha256 = "";

        var error = Assert.Throws<InvalidOperationException>(
            () => Deduper.Run(new Batch { Documents = [document] }));

        Assert.Contains("sha256", error.Message);
    }

    [Fact]
    public void EmailDateWithoutOffset_IsRejected()
    {
        var batch = new Batch
        {
            Documents = [Email("E1", "c1", "2024-03-05T14:22:00")],
        };

        var error = Assert.Throws<InvalidOperationException>(() => Deduper.Run(batch));

        Assert.Contains("invalid or missing sentDate", error.Message);
    }

    [Fact]
    public void AttachmentRootedInLooseFile_IsRejected()
    {
        var batch = new Batch
        {
            Documents =
            [
                Loose("L1", "c1", "loose"),
                Attachment("A1", "L1", "c1", "attachment"),
            ],
        };

        var error = Assert.Throws<InvalidOperationException>(() => Deduper.Run(batch));

        Assert.Contains("rooted in an email", error.Message);
    }

    [Fact]
    public void Output_IsIndependentOfInputOrder()
    {
        var documents = new[]
        {
            Loose("B", "c2", "duplicate"),
            Email("E1", "c1", "2024-03-05T19:22:00Z"),
            Attachment("A1", "E1", "c1", "attachment"),
            Loose("A", "c1", "duplicate"),
        };

        var forward = Deduper.Run(new Batch { Documents = documents.ToList() });
        var reverse = Deduper.Run(new Batch { Documents = documents.Reverse().ToList() });

        Assert.Equal(LoadFileWriter.ToCsv(forward.Rows), LoadFileWriter.ToCsv(reverse.Rows));
        Assert.Equal(forward.Suppressed, reverse.Suppressed);
    }

    [Fact]
    public void LoadFile_UsesFixedColumnsAndEscapesCsvFields()
    {
        var row = new LoadFileRow
        {
            DocId = "D1",
            FamilyId = "D1",
            Custodian = "custodian",
            DocType = "loose",
            FileName = "report, \"final\"\ncopy.txt",
            Hash = "hash",
        };

        var csv = LoadFileWriter.ToCsv([row]);

        Assert.Equal(
            "DocID,FamilyID,ParentID,Custodian,DocType,FileName,SentDate,Hash\n"
            + "D1,D1,,custodian,loose,\"report, \"\"final\"\"\ncopy.txt\",,hash\n",
            csv);
    }

    [Fact]
    public void SampleBatch_ProducesCompleteDeduplicatedFamiliesAndCompliantDates()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "batch-sample.json"));
        var batch = JsonSerializer.Deserialize<Batch>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var result = Deduper.Run(batch);

        Assert.Equal(12, result.Rows.Count);
        Assert.Equal(4, result.Suppressed);
        Assert.Equal(
            [
                "DOC-000001", "DOC-000002", "DOC-000003", "DOC-000004",
                "DOC-000005", "DOC-000006", "DOC-000007", "DOC-000008",
                "DOC-000012", "DOC-000014", "DOC-000015", "DOC-000016",
            ],
            result.Rows.Select(row => row.DocId));
        Assert.DoesNotContain(result.Rows, row => row.DocId == "DOC-000009");
        Assert.All(
            result.Rows.Where(row => row.FamilyId == "DOC-000001"),
            row => Assert.Equal("2024-03-05T19:22:00Z", row.SentDate));
    }
}

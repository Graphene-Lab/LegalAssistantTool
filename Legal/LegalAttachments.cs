using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AIOrchestrator.API.Legal;

/// <summary>One attachment, already reduced to plain text.</summary>
/// <param name="Name">File name as the agent sees it.</param>
/// <param name="Text">Extracted plain text.</param>
/// <param name="Note">How the text was obtained, or why it was not.</param>
internal sealed record LegalAttachment(string Name, string Text, string Note);

/// <summary>
/// Reads the documents attached to a case (plain text, Markdown, HTML, PDF, DOCX) into plain
/// text. Everything stays inside the workspace sandbox; a file that cannot be read becomes a
/// note instead of an error, so one bad attachment never blocks the opinion.
/// </summary>
internal static class LegalAttachments
{
    private static readonly string[] TextExtensions =
        [".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml", ".log", ".rtf"];

    private static readonly string[] HtmlExtensions = [".html", ".htm", ".xhtml"];

    /// <summary>Reads every attachment, skipping (with a note) the ones that cannot be read.</summary>
    /// <param name="agentPaths">Workspace-relative or sandbox-absolute paths.</param>
    /// <returns>One entry per requested attachment, in input order.</returns>
    public static List<LegalAttachment> Read(IEnumerable<string> agentPaths)
    {
        var result = new List<LegalAttachment>();
        foreach (var agentPath in agentPaths)
        {
            if (string.IsNullOrWhiteSpace(agentPath)) continue;
            if (!SandboxPath.TryResolve(agentPath, out var fullPath))
            {
                result.Add(new LegalAttachment(Path.GetFileName(agentPath), string.Empty, "percorso fuori dal workspace: allegato ignorato"));
                Log.LogStep($"Attachment rejected (outside sandbox): {agentPath}");
                continue;
            }

            if (!File.Exists(fullPath))
            {
                result.Add(new LegalAttachment(Path.GetFileName(fullPath), string.Empty, "file non trovato: allegato ignorato"));
                continue;
            }

            var extension = Path.GetExtension(fullPath).ToLowerInvariant();
            var name = Path.GetFileName(fullPath);
            try
            {
                var (text, note) = extension switch
                {
                    _ when TextExtensions.Contains(extension) => (File.ReadAllText(fullPath, Encoding.UTF8), "testo letto integralmente"),
                    _ when HtmlExtensions.Contains(extension) => (HtmlToText(File.ReadAllText(fullPath, Encoding.UTF8)), "HTML convertito in testo"),
                    ".pdf" => (ReadPdf(fullPath), "PDF estratto"),
                    ".docx" => (ReadDocx(fullPath), "DOCX estratto"),
                    _ => (string.Empty, $"formato {extension} non supportato: convertirlo in PDF, DOCX o testo")
                };

                Log.LogStep($"Attachment {name}: {text.Length} chars ({note})");
                result.Add(new LegalAttachment(name, LegalNormalizer.CleanText(text), note));
            }
            catch (Exception ex)
            {
                Log.LogStep($"Attachment {name} failed: {ex.GetType().Name}: {ex.Message}");
                result.Add(new LegalAttachment(name, string.Empty, $"lettura fallita ({ex.GetType().Name}): allegato ignorato"));
            }
        }

        return result;
    }

    private static string ReadPdf(string path)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        var builder = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            builder.AppendLine(page.Text);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string ReadDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml");
        if (entry == null) return string.Empty;
        using var stream = entry.Open();
        var xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var builder = new StringBuilder();
        foreach (var paragraph in xml.Descendants(w + "p"))
        {
            foreach (var run in paragraph.Descendants(w + "t"))
                builder.Append(run.Value);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>Converts an HTML document to plain text.</summary>
    /// <param name="html">HTML source.</param>
    /// <returns>Plain text.</returns>
    internal static string HtmlToText(string html)
    {
        var withoutScripts = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(withoutTags);
    }
}
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SnapDoc.Models;

namespace SnapDoc.Export;

/// <summary>
/// Exports captures to a Markdown step guide with images in a sibling assets folder.
/// This is the "wedge" output: capture a sequence of steps, get a clean how-to document.
/// </summary>
public sealed class MarkdownExporter : IExporter
{
    public string FormatName => "Markdown";
    public string Extension => ".md";

    public async Task ExportAsync(IReadOnlyList<Capture> captures, string outputPath, ExportOptions options)
    {
        // images live in "<file>_assets" next to the .md
        string baseDir = Path.GetDirectoryName(outputPath)!;
        string stem = Path.GetFileNameWithoutExtension(outputPath);
        string assetDir = options.AssetFolder ?? Path.Combine(baseDir, stem + "_assets");
        Directory.CreateDirectory(assetDir);

        var sb = new StringBuilder();
        sb.AppendLine($"# {options.DocumentTitle}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(options.Author))
        {
            sb.AppendLine($"*By {options.Author}*");
            sb.AppendLine();
        }

        for (int i = 0; i < captures.Count; i++)
        {
            var cap = captures[i];
            string imgName = $"{stem}_{i + 1:D2}.png";
            string imgPath = Path.Combine(assetDir, imgName);
            await File.WriteAllBytesAsync(imgPath, CaptureFlattener.FlattenToPng(cap));

            string heading = string.IsNullOrWhiteSpace(cap.Title) ? $"Step {i + 1}" : cap.Title;
            if (options.AsStepGuide)
                sb.AppendLine($"## Step {i + 1}: {heading}");
            else
                sb.AppendLine($"## {heading}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(cap.Caption))
            {
                sb.AppendLine(cap.Caption);
                sb.AppendLine();
            }

            // relative path so the .md is portable with its assets folder
            string rel = Path.Combine(Path.GetFileName(assetDir), imgName).Replace('\\', '/');
            sb.AppendLine($"![{heading}]({rel})");
            sb.AppendLine();

            if (options.IncludeOcrText && !string.IsNullOrWhiteSpace(cap.OcrText))
            {
                sb.AppendLine("> **Text in image:**");
                foreach (var line in cap.OcrText.Split('\n'))
                    sb.AppendLine($"> {line.TrimEnd()}");
                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), Encoding.UTF8);
    }
}

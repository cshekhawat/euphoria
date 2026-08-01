// DocsOnlyGuard
//
// Runs entirely locally — no GitHub/CI involved. Three modes:
//
//   VERIFY (default): verifies .cs files changed relative to a base git ref
//   differ ONLY in comment/whitespace trivia (no code changed), and flags
//   any non-ASCII characters introduced. Writes docs-verification-report.md.
//     dotnet run --project tools/DocsOnlyGuard -- [baseRef] [--report path]
//
//   SCAN: inventories the whole repo (not a diff) for public members
//   missing XML doc comments, grouped by folder, so you can prioritize
//   which files to document first. Writes docs-coverage-report.md.
//     dotnet run --project tools/DocsOnlyGuard -- --scan [--report path]
//
//   SCAN --all: same as SCAN, but includes EVERY .cs file regardless of
//   current documentation state (not just ones with gaps) — for a full
//   sweep rather than a gap-prioritized one. Also writes a ready-to-use
//   batch plan (docs-batches.md) chunking files into groups of --batch-size
//   (default 15) per layer, so you don't hand-copy filenames into the tracker.
//     dotnet run --project tools/DocsOnlyGuard -- --scan --all [--batch-size 15]

using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    private static int Main(string[] args)
    {
        var reportIndex = Array.IndexOf(args, "--report");
        var explicitReportPath = reportIndex >= 0 && reportIndex + 1 < args.Length
            ? args[reportIndex + 1]
            : null;

        var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
        if (repoRoot is null)
        {
            Console.Error.WriteLine("ERROR: not inside a git repository.");
            return 2;
        }

        if (args.Contains("--scan"))
        {
            var includeAll = args.Contains("--all");
            var batchSizeIndex = Array.IndexOf(args, "--batch-size");
            var batchSize = batchSizeIndex >= 0 && batchSizeIndex + 1 < args.Length
                && int.TryParse(args[batchSizeIndex + 1], out var bs) ? bs : 15;
            return RunScan(repoRoot, explicitReportPath ?? "docs-coverage-report.md", includeAll, batchSize);
        }

        var baseRef = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "HEAD";
        return RunVerify(repoRoot, baseRef, explicitReportPath ?? "docs-verification-report.md");
    }

    // ---------------------------------------------------------------
    // VERIFY MODE — comments-only diff check
    // ---------------------------------------------------------------

    private static int RunVerify(string repoRoot, string baseRef, string reportPath)
    {
        var changedFiles = GetChangedCsFiles(repoRoot, baseRef);
        var results = new List<FileResult>();

        foreach (var relativePath in changedFiles)
        {
            var fullPath = Path.Combine(repoRoot, relativePath);
            var newContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
            var oldContent = GetFileAtRef(repoRoot, baseRef, relativePath);
            results.Add(EvaluateFile(relativePath, oldContent, newContent));
        }

        WriteVerifyReport(reportPath, baseRef, results);

        var failed = results.Where(r => !r.Passed).ToList();
        Console.WriteLine();
        Console.WriteLine(changedFiles.Count == 0
            ? $"No changed .cs files found vs '{baseRef}'."
            : $"DocsOnlyGuard: {results.Count - failed.Count}/{results.Count} files passed.");

        foreach (var f in failed)
            Console.WriteLine($"  FAIL  {f.RelativePath}: {f.Summary}");

        Console.WriteLine($"Report written to {Path.GetFullPath(reportPath)}");
        return failed.Count == 0 ? 0 : 1;
    }

    private static FileResult EvaluateFile(string relativePath, string? oldContent, string? newContent)
    {
        if (newContent is null)
            return new FileResult(relativePath, true, "File deleted — nothing to verify.", []);

        if (oldContent is null)
        {
            var asciiIssuesNew = FindNonAscii(newContent);
            var passedNew = asciiIssuesNew.Count == 0;
            return new FileResult(
                relativePath, passedNew,
                passedNew ? "New file — ASCII OK (not a comment-diff candidate)." : "New file contains non-ASCII characters.",
                asciiIssuesNew);
        }

        var tokensOk = TokensAreEquivalent(oldContent, newContent, out var mismatchDetail);
        var asciiIssues = FindNonAscii(newContent);
        var passed = tokensOk && asciiIssues.Count == 0;

        var summaryParts = new List<string>();
        if (!tokensOk) summaryParts.Add("non-comment code changed");
        if (asciiIssues.Count > 0) summaryParts.Add($"{asciiIssues.Count} non-ASCII character(s) found");

        var detail = new List<string>();
        if (!tokensOk) detail.Add(mismatchDetail);
        detail.AddRange(asciiIssues);

        return new FileResult(
            relativePath, passed,
            passed ? "Comments/doc-comments only. ASCII OK." : string.Join("; ", summaryParts),
            detail);
    }

    // Roslyn tokens deliberately exclude comment/whitespace trivia — comments
    // are attached TO tokens, they are not tokens themselves. Comparing the
    // token sequence of the old and new file tells us, precisely, whether
    // any actual code changed.
    private static bool TokensAreEquivalent(string oldContent, string newContent, out string mismatchDetail)
    {
        var oldTokens = CSharpSyntaxTree.ParseText(oldContent).GetRoot()
            .DescendantTokens().Select(t => (t.RawKind, t.Text)).ToList();
        var newTokens = CSharpSyntaxTree.ParseText(newContent).GetRoot()
            .DescendantTokens().Select(t => (t.RawKind, t.Text)).ToList();

        if (oldTokens.Count == newTokens.Count && oldTokens.SequenceEqual(newTokens))
        {
            mismatchDetail = string.Empty;
            return true;
        }

        var minLen = Math.Min(oldTokens.Count, newTokens.Count);
        var i = 0;
        while (i < minLen && oldTokens[i] == newTokens[i]) i++;

        var oldSnippet = i < oldTokens.Count ? oldTokens[i].Text : "(end of file)";
        var newSnippet = i < newTokens.Count ? newTokens[i].Text : "(end of file)";
        mismatchDetail =
            $"First code difference at token #{i}: was '{oldSnippet}', now '{newSnippet}' " +
            $"(old had {oldTokens.Count} code tokens, new has {newTokens.Count}).";
        return false;
    }

    private static void WriteVerifyReport(string path, string baseRef, List<FileResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Docs-Only Verification Report");
        sb.AppendLine();
        sb.AppendLine($"Compared against: `{baseRef}`  ");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        if (results.Count == 0)
        {
            sb.AppendLine("No changed `.cs` files to verify.");
        }
        else
        {
            var passed = results.Count(r => r.Passed);
            sb.AppendLine($"**Result: {passed}/{results.Count} files passed.**");
            sb.AppendLine();
            sb.AppendLine("| File | Status | Notes |");
            sb.AppendLine("|---|---|---|");
            foreach (var r in results)
                sb.AppendLine($"| `{r.RelativePath}` | {(r.Passed ? "PASS" : "FAIL")} | {r.Summary} |");

            var failed = results.Where(r => !r.Passed).ToList();
            if (failed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Details for failed files");
                foreach (var f in failed)
                {
                    sb.AppendLine();
                    sb.AppendLine($"### `{f.RelativePath}`");
                    foreach (var d in f.Details)
                        sb.AppendLine($"- {d}");
                }
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    // ---------------------------------------------------------------
    // SCAN MODE — coverage inventory across the whole repo
    // ---------------------------------------------------------------

    private static int RunScan(string repoRoot, string reportPath, bool includeAll, int batchSize)
    {
        var csFiles = Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.EndsWith(".g.cs") && !f.EndsWith(".Designer.cs"))
            .ToList();

        var coverageResults = new List<CoverageResult>();
        foreach (var fullPath in csFiles)
        {
            var relativePath = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
            var content = File.ReadAllText(fullPath);
            coverageResults.Add(ScanFile(relativePath, content));
        }

        WriteCoverageReport(reportPath, coverageResults, includeAll);

        if (includeAll)
        {
            var batchPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".", "docs-batches.md");
            WriteBatchPlan(batchPath, coverageResults, batchSize);
            Console.WriteLine($"Batch plan written to {Path.GetFullPath(batchPath)}");
        }

        var totalMissing = coverageResults.Sum(r => r.UndocumentedCount);
        Console.WriteLine($"Scanned {coverageResults.Count} .cs files.");
        Console.WriteLine($"Total undocumented public/internal members: {totalMissing}");
        Console.WriteLine($"Report written to {Path.GetFullPath(reportPath)}");
        return 0;
    }

    private static CoverageResult ScanFile(string relativePath, string content)
    {
        var root = CSharpSyntaxTree.ParseText(content).GetRoot();

        // Public/internal types and members that SHOULD have XML docs,
        // per csharp-general.instructions.md, but don't.
        var candidates = root.DescendantNodes().Where(n =>
            n is ClassDeclarationSyntax or RecordDeclarationSyntax or InterfaceDeclarationSyntax
              or MethodDeclarationSyntax or PropertyDeclarationSyntax or ConstructorDeclarationSyntax);

        var missing = 0;
        var total = 0;
        foreach (var node in candidates)
        {
            if (!IsPublicOrInternal(node)) continue;
            total++;
            if (!HasXmlDocComment(node)) missing++;
        }

        var layer = ClassifyLayer(relativePath);
        return new CoverageResult(relativePath, layer, total, missing);
    }

    private static bool IsPublicOrInternal(SyntaxNode node)
    {
        var modifiers = node switch
        {
            MemberDeclarationSyntax m => m.Modifiers,
            _ => default,
        };
        if (modifiers.Count == 0) return false;
        return modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword));
    }

    private static bool HasXmlDocComment(SyntaxNode node)
        => node.GetLeadingTrivia().Any(t =>
            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
            t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));

    private static string ClassifyLayer(string relativePath)
    {
        if (relativePath.Contains("/Controllers/", StringComparison.OrdinalIgnoreCase)) return "Controllers";
        if (relativePath.Contains("/Services/", StringComparison.OrdinalIgnoreCase)) return "Services";
        if (relativePath.Contains("/Models/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/Entities/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/DTOs/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/Contracts/", StringComparison.OrdinalIgnoreCase)) return "Models/DTOs";
        if (relativePath.Contains("/Helpers/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/Utils/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/Extensions/", StringComparison.OrdinalIgnoreCase)) return "Helpers";
        return "Other";
    }

    private static void WriteCoverageReport(string path, List<CoverageResult> results, bool includeAll)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Docs Coverage Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (includeAll)
            sb.AppendLine("Mode: full inventory (every file listed, not just gaps).");
        sb.AppendLine();

        var relevant = includeAll ? results : results.Where(r => r.UndocumentedCount > 0).ToList();
        var ordered = relevant.OrderByDescending(r => r.UndocumentedCount).ToList();

        var withGaps = results.Where(r => r.UndocumentedCount > 0).ToList();
        sb.AppendLine($"**{withGaps.Count} of {results.Count} files have undocumented public/internal members "
            + $"({results.Sum(r => r.UndocumentedCount)} members total).**");
        sb.AppendLine();

        foreach (var layer in new[] { "Controllers", "Services", "Models/DTOs", "Helpers", "Other" })
        {
            var layerFiles = ordered.Where(r => r.Layer == layer).ToList();
            if (layerFiles.Count == 0) continue;

            sb.AppendLine($"## {layer} — {layerFiles.Count} files listed, {layerFiles.Sum(f => f.UndocumentedCount)} undocumented members");
            sb.AppendLine();
            sb.AppendLine("| File | Undocumented / Total members |");
            sb.AppendLine("|---|---|");
            foreach (var f in layerFiles)
                sb.AppendLine($"| `{f.RelativePath}` | {f.UndocumentedCount} / {f.TotalMembers} |");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    // Chunks every file (regardless of current documentation state) into
    // batches of at most batchSize per layer, ready to paste straight into
    // docs-rollout-tracker.md or hand to the docs-only agent directly —
    // for a full sweep rather than a gap-prioritized one.
    private static void WriteBatchPlan(string path, List<CoverageResult> results, int batchSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Docs Batch Plan (full sweep)");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Batch size: {batchSize} files. Every .cs file is included, regardless of current documentation state.");
        sb.AppendLine();
        sb.AppendLine("Copy each batch's file list into a docs-only agent request, one batch at a time.");
        sb.AppendLine();

        var batchNumber = 1;
        foreach (var layer in new[] { "Controllers", "Services", "Models/DTOs", "Helpers", "Other" })
        {
            var layerFiles = results.Where(r => r.Layer == layer)
                .OrderBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (layerFiles.Count == 0) continue;

            sb.AppendLine($"## {layer} ({layerFiles.Count} files)");
            sb.AppendLine();

            for (var i = 0; i < layerFiles.Count; i += batchSize)
            {
                var chunk = layerFiles.Skip(i).Take(batchSize).ToList();
                var layerSlug = layer.Replace("/", "-").ToLowerInvariant();
                sb.AppendLine($"### Batch {batchNumber} — branch `docs/{layerSlug}-batch-{(i / batchSize) + 1}` ({chunk.Count} files)");
                foreach (var f in chunk)
                    sb.AppendLine($"- [ ] `{f.RelativePath}`");
                sb.AppendLine();
                batchNumber++;
            }
        }

        File.WriteAllText(path, sb.ToString());
    }

    // ---------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------

    private static List<string> FindNonAscii(string content)
    {
        var issues = new List<string>();
        var lines = content.Split('\n');
        for (var lineNum = 0; lineNum < lines.Length; lineNum++)
        {
            foreach (var ch in lines[lineNum])
            {
                if (ch > 127)
                {
                    issues.Add($"Line {lineNum + 1}: non-ASCII character '{ch}' (U+{(int)ch:X4})");
                    break;
                }
            }
        }
        return issues;
    }

    private static List<string> GetChangedCsFiles(string repoRoot, string baseRef)
    {
        var output = RunGit(repoRoot, $"diff --name-only {baseRef} -- \"*.cs\"");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string? GetFileAtRef(string repoRoot, string baseRef, string relativePath)
    {
        var (output, exitCode) = RunGitWithExitCode(repoRoot, $"show {baseRef}:\"{relativePath.Replace('\\', '/')}\"");
        return exitCode == 0 ? output : null;
    }

    private static string RunGit(string repoRoot, string arguments) => RunGitWithExitCode(repoRoot, arguments).output;

    private static (string output, int exitCode) RunGitWithExitCode(string repoRoot, string arguments)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (output, process.ExitCode);
    }

    private static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private sealed record FileResult(string RelativePath, bool Passed, string Summary, List<string> Details);
    private sealed record CoverageResult(string RelativePath, string Layer, int TotalMembers, int UndocumentedCount);
}

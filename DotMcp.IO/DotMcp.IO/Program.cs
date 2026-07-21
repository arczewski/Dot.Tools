using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

// Record used by BulkReplaceAndInsertLines – must match JSON property names exactly
public record LineEdit
{
   [Required] public int StartIndex { get; init; }
   [Required] public int EndIndex { get; init; }
   [Required] public string[] Lines { get; init; } = Array.Empty<string>();
}

[McpServerToolType]
public static class FileSystemTools
{
    [McpServerTool,
     Description(
         "List subdirectories under the given path. Use searchPattern to filter directory names (supports wildcards). maxDepth controls recursion: 0 = immediate children only, 1 = one extra level, etc. Set a negative value for unlimited recursion. Avoid using '*' in large directory trees; prefer specific patterns and a limited maxDepth.")]
    public static IReadOnlyCollection<string> ListDirectories(string path, string searchPattern = "*", int maxDepth = 0)
    {
        if(string.IsNullOrEmpty(path))
            path = Directory.GetCurrentDirectory();
        if (maxDepth < 0)
            return Directory.GetDirectories(path, searchPattern, SearchOption.AllDirectories);
        if (maxDepth == 0)
            return Directory.GetDirectories(path, searchPattern, SearchOption.TopDirectoryOnly);

        var result = new List<string>();
        Recurse(path, 0);
        return result;

        void Recurse(string currentDir, int depth)
        {
            if (depth > maxDepth) return;
            foreach (var sub in Directory.GetDirectories(currentDir, searchPattern))
                result.Add(sub);
            if (depth == maxDepth) return;
            foreach (var sub in Directory.GetDirectories(currentDir))
                Recurse(sub, depth + 1);
        }
    }

    [McpServerTool,
     Description(
         "List files under the given path. Use searchPattern to filter file names (e.g., '*.cs', '*.txt'). maxDepth: 0 lists only files directly in the path, 1 adds immediate subdirectories, etc. Negative means unlimited. " +
         "By default, excludes common noise directories (obj, bin, node_modules, .git, .vs, .idea, dist, .next). Pass excludeFolders=[] (empty array) to disable filtering. " +
         "Be cautious with '*' in directories containing many files; prefer specific patterns and depth limits to avoid overwhelming results.")]
    public static IReadOnlyCollection<string> ListFiles(string path, string searchPattern = "*", int maxDepth = 0,
        [Description("OPTIONAL: Directory names to exclude from recursive search. Default excludes: obj, bin, node_modules, .git, .vs, .idea, dist, .next. Pass empty array [] to include all directories.")]
        string[]? excludeFolders = null)
    {
        if(string.IsNullOrEmpty(path))
            path = Directory.GetCurrentDirectory();

        var excluded = new HashSet<string>(
            excludeFolders ?? ["obj", "bin", "node_modules", ".git", ".vs", ".idea", "dist", ".next"],
            StringComparer.OrdinalIgnoreCase);

        if (maxDepth == 0)
            return Directory.GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly);

        if (maxDepth < 0 && excluded.Count == 0)
            return Directory.GetFiles(path, searchPattern, SearchOption.AllDirectories);

        var result = new List<string>();
        Recurse(path, 0);
        return result;

        void Recurse(string dir, int depth)
        {
            if (maxDepth >= 0 && depth > maxDepth) return;
            try
            {
                foreach (var file in Directory.GetFiles(dir, searchPattern))
                    result.Add(file);
            }
            catch
            {
                /* skip inaccessible directories */
            }
            if (maxDepth >= 0 && depth == maxDepth) return;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (excluded.Contains(Path.GetFileName(sub))) continue;
                Recurse(sub, depth + 1);
            }
        }
    }

    [McpServerTool,
     Description(
         "Read entire file content. Optionally prefixes each line with its 1-based number for line-based editing tools. Use only for reasonably sized files; for large files, consider ReadLinesFromFile.")]
    public static string ReadAllFileLines(string path,
        [Description("OPTIONAL: If true, prefixes each line with its number like '1: content'. Default is false (raw content).")]
        bool lineNumbers = false) =>
        lineNumbers
            ? string.Join(Environment.NewLine, File.ReadAllLines(path).Select((x, i) => $"{i + 1}: {x}"))
            : File.ReadAllText(path);

    [McpServerTool,
     Description(
         "Read a subset of lines from a file, starting at line index 'from' (0-based offset) and reading 'count' lines. " +
         "Suitable for reading large files in chunks. " +
         "Example: from=0, count=10 reads the first 10 lines. from=10, count=5 reads lines 11-15.")]
    public static string ReadLinesFromFile(string path, int from, int count,
        [Description("OPTIONAL: If true, prefixes each line with its number like '5: content'. Default is false (raw content).")]
        bool lineNumbers = false) =>
        lineNumbers
            ? string.Join(Environment.NewLine, File.ReadLines(path).Skip(from).Take(count).Select((x, i) => $"{i + from + 1}: {x}"))
            : string.Join(Environment.NewLine, File.ReadLines(path).Skip(from).Take(count));

    [McpServerTool,
     Description(
         "Search for a regex pattern in a single file. Returns matches with (line, column) and the matched text. Note: the whole file is read into memory.")]
    public static string SearchInFile(string path, string searchPattern)
    {
        if (!File.Exists(path)) return "Error: File not found.";

        try
        {
            var content = File.ReadAllText(path);
            var matches = Regex.Matches(content, searchPattern);
            if (matches.Count == 0) return "No matches found.";

            return string.Join(Environment.NewLine, matches.Select(m =>
            {
                var line = content[..m.Index].Count(c => c == '\n') + 1;
                var lastNl = m.Index > 0 ? content.LastIndexOf('\n', m.Index - 1) : -1;
                var col = lastNl == -1 ? m.Index + 1 : m.Index - lastNl;
                return $"({line}, {col}) - {m.Value}";
            }));
        }
        catch (ArgumentException ex)
        {
            return $"Error: Invalid regex pattern. {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool,
     Description(
         "Search for a regex pattern across files in a directory tree. Use includeExtensions to limit to specific file types (e.g., '.cs,.txt'). ignoreFolders skips given directory names (default: common build/ignore folders). maxDepth controls recursion depth (-1 = unlimited, 0 = only the given directory, 1 = one level of subdirectories, etc.). Large searches can be slow; narrow scope with filters and depth.")]
    public static string SearchInDirectory(string directoryPath, string searchPattern,
        string[]? ignoreFolders = null, string[]? includeExtensions = null, int maxDepth = -1)
    {
        if(string.IsNullOrEmpty(directoryPath))
            directoryPath = Directory.GetCurrentDirectory();
        if (!Directory.Exists(directoryPath)) return $"Error: Directory not found: {directoryPath}";

        var excluded = new HashSet<string>(
            ignoreFolders ?? [".git", "node_modules", "obj", "bin", ".vs", ".next", ".idea", "dist"],
            StringComparer.OrdinalIgnoreCase);

        var whitelist = includeExtensions?
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            var sb = new StringBuilder();
            foreach (var file in WalkFiles(directoryPath, excluded, whitelist, maxDepth))
            {
                string content;
                try
                {
                    content = File.ReadAllText(file);
                }
                catch
                {
                    continue;
                }

                var matches = Regex.Matches(content, searchPattern);
                if (matches.Count == 0) continue;

                sb.AppendLine($"File: {file}");
                foreach (Match m in matches)
                {
                    var line = content[..m.Index].Count(c => c == '\n') + 1;
                    var lastNl = m.Index > 0 ? content.LastIndexOf('\n', m.Index - 1) : -1;
                    var col = lastNl == -1 ? m.Index + 1 : m.Index - lastNl;
                    sb.AppendLine($"  -> ({line}, {col})");
                }
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "No matches found.";
        }
        catch (ArgumentException ex)
        {
            return $"Error: Invalid regex pattern. {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool,
     Description(
         "Write full text content to a file, overwriting if it exists. Set returnFullFileContentInResponse to true to include the new file content in the response.")]
    public static string WriteFile(string path, string content, bool returnFullFileContentInResponse = false)
    {
        try
        {
            File.WriteAllText(path, content);
            return $"Successfully wrote {content.Length} characters to {path}{Environment.NewLine}" +
                   (returnFullFileContentInResponse ? File.ReadAllText(path) : "");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool,
     Description(
         "Insert new lines into a file BEFORE a given line number, without replacing or removing any existing lines. " +
         "This is a pure insertion: existing content shifts down. " +
         "WHEN TO USE: When you want to add lines (e.g., a new 'using' statement at the top) without touching existing content. " +
         "EXAMPLE 1: path='file.cs', beforeLine=1, lines=['using System.Text;'] - inserts at the very top, existing line 1 becomes line 2. " +
         "EXAMPLE 2: path='file.cs', beforeLine=5, lines=['    // new comment', '    var x = 1;'] - inserts two lines before what was line 5. " +
         "EXAMPLE 3: path='file.cs', beforeLine=999 (beyond end), lines=['// appended'] - appends at end of file. " +
         "REQUIRED: path, beforeLine (1-based), lines (array of strings to insert). " +
         "OUTPUT: Confirmation message. Set returnFullFileContentInResponse=true to see final content.")]
    public static string InsertLinesAt(
        [Description("REQUIRED: Path to file.")] [Required] string path,
        [Description("REQUIRED: 1-based line number to insert BEFORE. Line 1 = insert at top. If beyond file length, appends at end.")] [Required] int beforeLine,
        [Description("REQUIRED: Array of line strings to insert. Each element becomes one line in the file.")] [Required] string[] lines,
        [Description("OPTIONAL: If true, returns the full file content after insertion.")] bool returnFullFileContentInResponse = false)
    {
        try
        {
            if (!File.Exists(path))
                return "Error: File not found.";

            var fileLines = File.ReadAllLines(path).ToList();

            // Clamp insertion point: 1-based beforeLine → 0-based index
            int insertAt = Math.Clamp(beforeLine - 1, 0, fileLines.Count);

            fileLines.InsertRange(insertAt, lines);

            File.WriteAllLines(path, fileLines);
            return $"Successfully inserted {lines.Length} line(s) before line {beforeLine} in {path}{Environment.NewLine}" +
                   (returnFullFileContentInResponse ? File.ReadAllText(path) : "");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool,
     Description(
         "Applies multiple line replacements in one atomic operation. " +
         "Each edit is an object with properties: startIndex (1‑based), endIndex (1‑based, inclusive), lines (array of strings to insert). " +
         "Edits are applied from the last line to the first, so the startIndex/endIndex values must refer to the file BEFORE any edits. " +
         "The file is only modified once; line numbering shifts are handled internally. " +
         "Set returnFullFileContentInResponse to true to get the final file content. " +
         "REPLACE: startIndex=2, endIndex=3, lines=['new line 2', 'new line 3'] - replaces lines 2-3 with new content. " +
         "DELETE: startIndex=5, endIndex=6, lines=[] - removes lines 5-6. " +
         "INSERT BEFORE: startIndex=4, endIndex=3, lines=['inserted line'] - inserts before line 4 without removing anything (endIndex < startIndex means pure insert). " +
         "NOTE: For simple insertions, prefer the InsertLinesAt tool which is simpler and less error-prone. " +
         "Both startIndex and endIndex is required!")]
    public static string BulkReplaceAndInsertLines(
        [Required] string path,
        [Required] LineEdit[] edits,
        bool returnFullFileContentInResponse = false)
    {
        try
        {
            if (!File.Exists(path))
                return "Error: File not found.";

            var fileLines = File.ReadAllLines(path).ToList();

            // Sort edits descending by startIndex (bottom‑up)
            var ordered = edits
                .OrderByDescending(e => e.StartIndex)
                .ToList();

            foreach (var edit in ordered)
            {
                int s = edit.StartIndex - 1; // 0-based
                int e = edit.EndIndex - 1;   // 0-based
                var newLines = edit.Lines;

                if (s < 0) continue;

                // INSERT mode: endIndex < startIndex means "insert before startIndex without removing"
                if (e < s)
                {
                    int insertAt = Math.Min(s, fileLines.Count);
                    fileLines.InsertRange(insertAt, newLines);
                    continue;
                }

                // REPLACE/DELETE mode
                if (s >= fileLines.Count) continue;

                int count = e - s + 1;
                if (s + count > fileLines.Count)
                    count = fileLines.Count - s;
                fileLines.RemoveRange(s, count);
                fileLines.InsertRange(s, newLines);
            }

            File.WriteAllLines(path, fileLines);
            return $"Successfully applied {edits.Length} edits to {path}{Environment.NewLine}" +
                   (returnFullFileContentInResponse ? File.ReadAllText(path) : "");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
         "Remove lines by their indices (1-based). Lines beyond the file length are ignored. " +
         "Set returnFullFileContentInResponse to true to return the updated file content. " +
         "Example: To remove lines 2 and 5, use lineIndexes=[2, 5]. Line numbering is based on the original file.")]
    public static string RemoveLines(string path, int[] lineIndexes, bool returnFullFileContentInResponse = false)
    {
        try
        {
            var existing = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            foreach (var lineIndex in lineIndexes.OrderDescending())
            {
                if (lineIndex - 1 >= existing.Count)
                    continue;
                existing.RemoveAt(lineIndex - 1);
            }
            File.WriteAllLines(path, existing);
            return $"Successfully removed {lineIndexes.Length} lines{Environment.NewLine}" +
                   (returnFullFileContentInResponse ? File.ReadAllText(path) : "");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description(
         "Find exact text in a file and replace it with new text. No line numbers needed - just provide the old text and new text. " +
         "BEST PRACTICE: This is the preferred way to edit files. Simpler and less error-prone than BulkReplaceAndInsertLines. " +
         "WHEN TO USE: Any time you want to modify existing code - change a method, update a statement, fix a bug. " +
         "HOW IT WORKS: Finds the EXACT oldText string in the file and replaces it with newText. Match must be unique (appears exactly once). " +
         "EXAMPLE 1: oldText='Console.WriteLine(\"hello\");', newText='Console.WriteLine(\"world\");' - simple replacement. " +
         "EXAMPLE 2: oldText='public void Old()\\n{\\n    // old\\n}', newText='public void New()\\n{\\n    // new\\n}' - multi-line replace. " +
         "TIPS: Include enough surrounding context in oldText to make the match unique. If the match isn't found or appears multiple times, the tool will tell you. " +
         "For insertions without replacing, use InsertLinesAt. For full file rewrites, use WriteFile.")]
    public static string ReplaceText(
        [Description("REQUIRED: Path to the file to edit.")] [Required] string path,
        [Description("REQUIRED: The exact text to find in the file. Must match exactly (including whitespace/newlines). Must appear exactly once.")] [Required] string oldText,
        [Description("REQUIRED: The text to replace it with. Can be empty string to delete the matched text.")] [Required] string newText,
        [Description("OPTIONAL: If true, returns the full file content after replacement.")] bool returnFullFileContentInResponse = false)
    {
        try
        {
            if (!File.Exists(path))
                return "Error: File not found.";

            var content = File.ReadAllText(path);

            var index = content.IndexOf(oldText, StringComparison.Ordinal);
            if (index == -1)
                return "Error: oldText not found in file. Make sure it matches exactly including whitespace and line endings. " +
                       $"File length: {content.Length} chars. Searched for {oldText.Length} chars.";

            var secondIndex = content.IndexOf(oldText, index + 1, StringComparison.Ordinal);
            if (secondIndex != -1)
                return $"Error: oldText appears multiple times in file (at least at positions {index} and {secondIndex}). " +
                       "Include more surrounding context to make the match unique.";

            var result = string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
            File.WriteAllText(path, result);

            var lineNum = content[..index].Count(c => c == '\n') + 1;
            return $"Successfully replaced text at line {lineNum} in {path}{Environment.NewLine}" +
                   (returnFullFileContentInResponse ? File.ReadAllText(path) : "");
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static IEnumerable<string> WalkFiles(string root, HashSet<string> excluded, HashSet<string>? whitelist, int maxDepth = -1)
    {
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((root, 0));
        while (stack.TryPop(out var state))
        {
            var dir = state.dir;
            var depth = state.depth;
            foreach (var file in Directory.EnumerateFiles(dir))
                if (whitelist == null || whitelist.Contains(Path.GetExtension(file)))
                    yield return file;

            if (maxDepth >= 0 && depth >= maxDepth) continue;

            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!excluded.Contains(Path.GetFileName(sub)))
                    stack.Push((sub, depth + 1));
        }
    }
}
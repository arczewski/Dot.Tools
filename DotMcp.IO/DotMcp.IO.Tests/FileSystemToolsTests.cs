namespace DotMcp.IO.Tests;

public class FileSystemToolsTests : IDisposable
{
    private readonly string testDir;

    public FileSystemToolsTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"DotMcpIO_Test_{Guid.NewGuid()}");
        Directory.CreateDirectory(testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDir))
            Directory.Delete(testDir, true);
    }

    // === ListDirectories ===

    [Fact]
    public void ListDirectories_ReturnsImmediateAndNested()
    {
        var l1 = Directory.CreateDirectory(Path.Combine(testDir, "l1"));
        Directory.CreateDirectory(Path.Combine(l1.FullName, "l2"));

        Assert.Single(FileSystemTools.ListDirectories(testDir, "*", 0));
        Assert.Equal(2, FileSystemTools.ListDirectories(testDir, "*", 1).Count);
        Assert.Equal(2, FileSystemTools.ListDirectories(testDir, "*", -1).Count);
    }

    [Fact]
    public void ListDirectories_PatternFilters()
    {
        Directory.CreateDirectory(Path.Combine(testDir, "test_a"));
        Directory.CreateDirectory(Path.Combine(testDir, "other"));

        Assert.Single(FileSystemTools.ListDirectories(testDir, "test_*", 0));
    }

    // === ListFiles ===

    [Fact]
    public void ListFiles_ReturnsFilesAndRespectsDepth()
    {
        var sub = Directory.CreateDirectory(Path.Combine(testDir, "sub"));
        File.WriteAllText(Path.Combine(testDir, "root.txt"), "r");
        File.WriteAllText(Path.Combine(sub.FullName, "sub.txt"), "s");

        Assert.Single(FileSystemTools.ListFiles(testDir, "*", 0));
        Assert.Equal(2, FileSystemTools.ListFiles(testDir, "*", 1).Count);
        Assert.Equal(2, FileSystemTools.ListFiles(testDir, "*", -1).Count);
    }

    [Fact]
    public void ListFiles_ExcludesFoldersByDefault()
    {
        var objDir = Directory.CreateDirectory(Path.Combine(testDir, "obj"));
        var binDir = Directory.CreateDirectory(Path.Combine(testDir, "bin"));
        File.WriteAllText(Path.Combine(objDir.FullName, "junk.cs"), "x");
        File.WriteAllText(Path.Combine(binDir.FullName, "junk.dll"), "x");
        File.WriteAllText(Path.Combine(testDir, "real.cs"), "x");

        var result = FileSystemTools.ListFiles(testDir, "*", -1);
        Assert.Single(result);
        Assert.Contains("real.cs", result.First());
    }

    [Fact]
    public void ListFiles_ExcludeOverrideAllowsAll()
    {
        var objDir = Directory.CreateDirectory(Path.Combine(testDir, "obj"));
        File.WriteAllText(Path.Combine(objDir.FullName, "junk.cs"), "x");
        File.WriteAllText(Path.Combine(testDir, "real.cs"), "x");

        var result = FileSystemTools.ListFiles(testDir, "*", -1, excludeFolders: []);
        Assert.Equal(2, result.Count);
    }

    // === ReadAllFileLines ===

    [Fact]
    public void ReadAllFileLines_DefaultReturnsRawContent()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllText(f, "line1\nline2\nline3");

        var result = FileSystemTools.ReadAllFileLines(f);
        Assert.DoesNotContain("1:", result);
        Assert.Equal("line1\nline2\nline3", result);
    }

    [Fact]
    public void ReadAllFileLines_WithLineNumbers()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllLines(f, ["a", "b"]);

        var result = FileSystemTools.ReadAllFileLines(f, lineNumbers: true);
        Assert.Contains("1: a", result);
        Assert.Contains("2: b", result);
    }

    // === ReadLinesFromFile ===

    [Fact]
    public void ReadLinesFromFile_ReturnsSubsetRawByDefault()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllLines(f, ["a", "b", "c", "d", "e"]);

        var result = FileSystemTools.ReadLinesFromFile(f, 1, 2);
        Assert.DoesNotContain("2:", result);
        Assert.Contains("b", result);
        Assert.Contains("c", result);
    }

    [Fact]
    public void ReadLinesFromFile_WithLineNumbers()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllLines(f, ["a", "b", "c"]);

        var result = FileSystemTools.ReadLinesFromFile(f, 1, 2, lineNumbers: true);
        Assert.Contains("2: b", result);
        Assert.Contains("3: c", result);
    }

    // === SearchInFile ===

    [Fact]
    public void SearchInFile_FindsMatchesWithPositions()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllText(f, "xhello world\nfoo\nxhello again");

        var result = FileSystemTools.SearchInFile(f, "hello");
        Assert.Contains("(1, 2)", result);
        Assert.Contains("(3, 2)", result);
    }

    [Fact]
    public void SearchInFile_NoMatch_ReturnsMessage()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllText(f, "nothing here");

        Assert.Equal("No matches found.", FileSystemTools.SearchInFile(f, "xyz"));
    }

    // === SearchInDirectory ===

    [Fact]
    public void SearchInDirectory_FindsAcrossFiles()
    {
        File.WriteAllText(Path.Combine(testDir, "a.cs"), "hello");
        File.WriteAllText(Path.Combine(testDir, "b.txt"), "world");

        var result = FileSystemTools.SearchInDirectory(testDir, "hello", includeExtensions: [".cs"]);
        Assert.Contains("a.cs", result);
        Assert.DoesNotContain("b.txt", result);
    }

    [Fact]
    public void SearchInDirectory_IgnoresFoldersAndRespectsDepth()
    {
        var ignored = Directory.CreateDirectory(Path.Combine(testDir, "node_modules"));
        File.WriteAllText(Path.Combine(ignored.FullName, "f.js"), "secret");
        File.WriteAllText(Path.Combine(testDir, "root.js"), "visible");

        Assert.DoesNotContain("node_modules", FileSystemTools.SearchInDirectory(testDir, "secret"));
    }

    // === WriteFile ===

    [Fact]
    public void WriteFile_CreatesAndOverwrites()
    {
        var f = Path.Combine(testDir, "out.txt");
        FileSystemTools.WriteFile(f, "first");
        Assert.Equal("first", File.ReadAllText(f));

        FileSystemTools.WriteFile(f, "second");
        Assert.Equal("second", File.ReadAllText(f));
    }

    // === InsertLinesAt ===

    [Fact]
    public void InsertLinesAt_InsertsWithoutDisplacing()
    {
        var f = Path.Combine(testDir, "test.cs");
        File.WriteAllLines(f, ["using A;", "using B;", "", "var x = 1;"]);

        FileSystemTools.InsertLinesAt(f, 1, ["using Z;"]);

        var lines = File.ReadAllLines(f);
        Assert.Equal("using Z;", lines[0]);
        Assert.Equal("using A;", lines[1]);
        Assert.Equal("using B;", lines[2]);
        Assert.Equal("var x = 1;", lines[4]);
    }

    [Fact]
    public void InsertLinesAt_BeyondEnd_Appends()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllLines(f, ["a", "b"]);

        FileSystemTools.InsertLinesAt(f, 999, ["c"]);
        Assert.Equal("c", File.ReadAllLines(f).Last());
    }

    // === BulkReplaceAndInsertLines ===

    [Fact]
    public void BulkReplace_ReplacesRange()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllLines(f, ["a", "b", "c", "d"]);

        FileSystemTools.BulkReplaceAndInsertLines(f, [new LineEdit { StartIndex = 2, EndIndex = 3, Lines = ["X", "Y"] }]);

        var lines = File.ReadAllLines(f);
        Assert.Equal(["a", "X", "Y", "d"], lines);
    }

    [Fact]
    public void BulkReplace_InsertMode_EndIndexLessThanStart()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllLines(f, ["a", "b"]);

        FileSystemTools.BulkReplaceAndInsertLines(f, [new LineEdit { StartIndex = 1, EndIndex = 0, Lines = ["Z"] }]);

        var lines = File.ReadAllLines(f);
        Assert.Equal(["Z", "a", "b"], lines);
    }

    [Fact]
    public void BulkReplace_DeleteMode_EmptyLines()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllLines(f, ["a", "b", "c"]);

        FileSystemTools.BulkReplaceAndInsertLines(f, [new LineEdit { StartIndex = 2, EndIndex = 2, Lines = [] }]);

        Assert.Equal(["a", "c"], File.ReadAllLines(f));
    }

    // === RemoveLines ===

    [Fact]
    public void RemoveLines_RemovesCorrectIndices()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllLines(f, ["a", "b", "c", "d", "e"]);

        FileSystemTools.RemoveLines(f, [1, 3, 5]);
        Assert.Equal(["b", "d"], File.ReadAllLines(f));
    }

    // === ReplaceText ===

    [Fact]
    public void ReplaceText_SimpleReplace()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllText(f, "hello world");

        var result = FileSystemTools.ReplaceText(f, "hello", "goodbye");
        Assert.Contains("Successfully replaced", result);
        Assert.Equal("goodbye world", File.ReadAllText(f));
    }

    [Fact]
    public void ReplaceText_MultiLineWithRealNewlines()
    {
        var f = Path.Combine(testDir, "test.cs");
        File.WriteAllText(f, "public void Old()\n{\n    // old\n}\n// end");

        FileSystemTools.ReplaceText(f,
            "public void Old()\n{\n    // old\n}",
            "public void New()\n{\n    // new\n}");

        var content = File.ReadAllText(f);
        Assert.Contains("public void New()", content);
        Assert.Contains("// new", content);
        Assert.Contains("// end", content);
        Assert.DoesNotContain("Old", content);
    }

    [Fact]
    public void ReplaceText_NotFound_ReturnsError()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllText(f, "hello world");

        var result = FileSystemTools.ReplaceText(f, "nonexistent", "x");
        Assert.Contains("Error: oldText not found", result);
        Assert.Equal("hello world", File.ReadAllText(f));
    }

    [Fact]
    public void ReplaceText_Duplicate_ReturnsError()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllText(f, "aaa bbb aaa");

        var result = FileSystemTools.ReplaceText(f, "aaa", "zzz");
        Assert.Contains("appears multiple times", result);
        Assert.Equal("aaa bbb aaa", File.ReadAllText(f));
    }

    [Fact]
    public void ReplaceText_WindowsCRLF_Preserved()
    {
        var f = Path.Combine(testDir, "test.cs");
        File.WriteAllText(f, "line1\r\nline2\r\nline3");

        FileSystemTools.ReplaceText(f, "line1\r\nline2", "new1\r\nnew2");
        Assert.Equal("new1\r\nnew2\r\nline3", File.ReadAllText(f));
    }

    [Fact]
    public void ReplaceText_UsingInsertion_Pattern()
    {
        var f = Path.Combine(testDir, "Program.cs");
        File.WriteAllText(f, "using System.Diagnostics;\nusing System.Text;\n\nvar x = 1;");

        FileSystemTools.ReplaceText(f,
            "using System.Diagnostics;",
            "using System.ClientModel;\nusing System.Diagnostics;");

        var content = File.ReadAllText(f);
        Assert.Contains("using System.ClientModel;", content);
        Assert.Contains("using System.Diagnostics;", content);
        Assert.Contains("using System.Text;", content);
        Assert.Contains("var x = 1;", content);
    }

    [Fact]
    public void ReplaceText_Delete_EmptyNewText()
    {
        var f = Path.Combine(testDir, "test.txt");
        File.WriteAllText(f, "keep remove keep");

        FileSystemTools.ReplaceText(f, " remove", "");
        Assert.Equal("keep keep", File.ReadAllText(f));
    }
}

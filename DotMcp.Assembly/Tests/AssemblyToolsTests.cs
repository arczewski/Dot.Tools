using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace DotMcp.Assembly.Tests;

public class AssemblyToolsTests
{
    private readonly string _testAssemblyPath;

    public AssemblyToolsTests()
    {
        _testAssemblyPath = typeof(AssemblyToolsTests).Assembly.Location;
    }

    [Fact]
    public void BrowseAssembly_ValidPath_ReturnsTypes()
    {
        var systemPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.dll");
        var result = AssemblyTools.BrowseAssembly(systemPath, null, 10, 5);
        
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.DoesNotContain("Error", result);
    }

    [Fact]
    public void BrowseAssembly_WithFilter_ReturnsFilteredTypes()
    {
        var systemPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.dll");
        var result = AssemblyTools.BrowseAssembly(systemPath, "*Exception", 10, 5);
        
        Assert.NotNull(result);
        Assert.Contains("Exception", result);
    }

    [Fact]
    public void BrowseAssembly_InvalidPath_ReturnsError()
    {
        var result = AssemblyTools.BrowseAssembly("/invalid/path/assembly.dll", null, 10, 5);
        
        Assert.NotNull(result);
        Assert.Contains("System.IO", result);
    }

    [Fact]
    public void FindTypeInAssembly_ExistingType_ReturnsTypeInfo()
    {
        var systemPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.dll");
        var result = AssemblyTools.FindTypeInAssembly(systemPath, "String");
        
        Assert.NotNull(result);
        Assert.Contains("Type:", result);
        Assert.Contains("Namespace:", result);
        Assert.Contains("Methods:", result);
    }

    [Fact]
    public void FindTypeInAssembly_NonExistingType_ReturnsNotFound()
    {
        var systemPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.dll");
        var result = AssemblyTools.FindTypeInAssembly(systemPath, "NonExistingType123");
        
        Assert.NotNull(result);
        Assert.Contains("not found", result);
    }

    [Fact]
    public void GetMethodsOnType_ExistingType_ReturnsMethods()
    {
        var systemPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.dll");
        var result = AssemblyTools.GetMethodsOnType(systemPath, "String", false, 10);
        
        Assert.NotNull(result);
        Assert.Contains("Methods on type:", result);
        Assert.Contains("public", result);
    }

    [Fact]
    public void GetMethodsOnType_WithInherited_ReturnsMoreMethods()
    {
        var systemPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.dll");
        var withoutInherited = AssemblyTools.GetMethodsOnType(systemPath, "String", false, 100);
        var withInherited = AssemblyTools.GetMethodsOnType(systemPath, "String", true, 100);
        
        Assert.True(withInherited.Length >= withoutInherited.Length);
    }

    [Fact]
    public void GetPropertiesOnType_ExistingType_ReturnsProperties()
    {
        var systemPath = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.dll");
        var result = AssemblyTools.GetPropertiesOnType(systemPath, "String", false);
        
        Assert.NotNull(result);
        Assert.Contains("Properties", result);
    }

    [Fact]
    public void SearchTypesInDirectory_ValidDirectory_ReturnsTypes()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var result = AssemblyTools.SearchTypesInDirectory(runtimeDir, "*Exception", 5);
        
        Assert.NotNull(result);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void SearchTypesInDirectory_NoMatches_ReturnsNoResults()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var result = AssemblyTools.SearchTypesInDirectory(runtimeDir, "XYZ123NonExisting*", 5);
        
        Assert.NotNull(result);
        Assert.Contains("No types matching", result);
    }

    [Fact]
    public async Task GetIntellisense_SimpleCode_ReturnsCompletions()
    {
        var code = "var x = \"test\";\nx.";
        var result = await AssemblyTools.GetIntellisense(code, 1, 2, null, 10);
        
        Assert.NotNull(result);
        Assert.Contains("Completions at line 1, column 2", result);
    }

    [Fact]
    public async Task GetIntellisense_ConsoleClass_ReturnsConsoleMethods()
    {
        var code = "using System;\nConsole.";
        var result = await AssemblyTools.GetIntellisense(code, 1, 8, null, 20);
        
        Assert.NotNull(result);
        Assert.Contains("WriteLine", result);
    }

    [Fact]
    public async Task GetIntellisense_ListType_ReturnsListMethods()
    {
        var code = "using System.Collections.Generic;\nvar list = new List<string>();\nlist.";
        var result = await AssemblyTools.GetIntellisense(code, 2, 5, null, 20);
        
        Assert.NotNull(result);
        Assert.Contains("Add", result);
    }

    [Fact]
    public async Task GetIntellisense_InvalidLineNumber_ReturnsError()
    {
        var code = "var x = 1;";
        var result = await AssemblyTools.GetIntellisense(code, 10, 0, null, 10);
        
        Assert.NotNull(result);
        Assert.Contains("out of range", result);
    }

    [Fact]
    public async Task GetIntellisense_InvalidColumnNumber_ReturnsError()
    {
        var code = "var x = 1;";
        var result = await AssemblyTools.GetIntellisense(code, 0, 100, null, 10);
        
        Assert.NotNull(result);
        Assert.Contains("out of range", result);
    }
}

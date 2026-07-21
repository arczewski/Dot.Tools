# .NET Assembly Inspector MCP

A Model Context Protocol (MCP) server that provides tools for inspecting .NET assemblies and providing IntelliSense-style code completion using Microsoft Roslyn.

## Available Tools

### 1. BrowseAssembly
**Purpose:** Browse all types, methods, and properties in a .NET DLL or EXE file.

**When to use:** When you want to see what's inside a compiled .NET assembly.

**Parameters:**
- `path` (required): Full absolute path to .dll or .exe file
- `typeNameFilter` (optional): Wildcard filter (e.g., `*Controller`, `*Service`)
- `maxTypes` (optional): Max types to show (default 100, -1 for all)
- `maxMembersPerType` (optional): Max members per type (default 20, -1 for all)

**Example:**
```json
{
  "path": "/Users/me/MyApp.dll",
  "typeNameFilter": "*Controller",
  "maxTypes": 50,
  "maxMembersPerType": 10
}
```

---

### 2. FindTypeInAssembly
**Purpose:** Find a specific class by exact name and get its metadata.

**When to use:** When you know the exact class name and want details (namespace, base class, interfaces, member counts).

**Parameters:**
- `path` (required): Full absolute path to .dll or .exe file
- `typeName` (required): Exact class name (e.g., `UserController` or `MyApp.Controllers.UserController`)

**Example:**
```json
{
  "path": "/Users/me/MyApp.dll",
  "typeName": "UserController"
}
```

**Output includes:**
- Full type name and namespace
- Base type
- Implemented interfaces
- Is abstract/sealed/generic
- Count of methods, properties, fields

---

### 3. GetMethodsOnType
**Purpose:** Get all methods (functions) on a specific class.

**When to use:** When you want to see what methods a class has with their signatures.

**Parameters:**
- `path` (required): Full absolute path to .dll or .exe file
- `typeName` (required): Class name to inspect
- `includeInherited` (optional): Include methods from base classes (default false)
- `maxMethods` (optional): Max methods to show (default 50, -1 for all)

**Example:**
```json
{
  "path": "/Users/me/MyApp.dll",
  "typeName": "UserService",
  "includeInherited": false,
  "maxMethods": 50
}
```

**Output format:**
```
public string GetUserName(int userId)
public static void DeleteUser(int userId)
public virtual async Task<User> FindUserAsync(string email)
```

---

### 4. GetPropertiesOnType
**Purpose:** Get all properties and fields on a specific class.

**When to use:** When you want to see what data members (properties/fields) a class has.

**Parameters:**
- `path` (required): Full absolute path to .dll or .exe file
- `typeName` (required): Class name to inspect
- `includeInherited` (optional): Include properties from base classes (default false)

**Example:**
```json
{
  "path": "/Users/me/MyApp.dll",
  "typeName": "User",
  "includeInherited": false
}
```

**Output format:**
```
Properties:
  string Name { get; set; }
  int Age { get; set; }
  DateTime CreatedAt { get; }

Fields:
  public static readonly int MaxNameLength
```

---

### 5. SearchTypesInDirectory
**Purpose:** Search for classes matching a pattern across ALL DLL files in a directory.

**When to use:** When you don't know which DLL contains the class you're looking for.

**Parameters:**
- `directoryPath` (required): Folder containing .dll or .exe files
- `typePattern` (required): Wildcard pattern (e.g., `*Controller`, `*Service`)
- `maxResults` (optional): Max results across all assemblies (default 50)

**Example:**
```json
{
  "directoryPath": "/Users/me/project/bin/Debug",
  "typePattern": "*Controller",
  "maxResults": 50
}
```

**Output format:**
```
MyApp.Controllers.UserController (in MyApp.dll)
MyApp.Controllers.ProductController (in MyApp.dll)
MyApp.Api.ApiController (in MyApp.Api.dll)
```

---

### 6. GetProjectIntellisense ⭐ NEW - Project-Aware Roslyn IntelliSense
**Purpose:** Get IntelliSense-style code completion for C# code with FULL PROJECT CONTEXT - all NuGet packages, project references, custom types automatically loaded.

**When to use:** When you have a .csproj file and want completions that include your project's libraries (ASP.NET Core, Entity Framework, your own classes, etc.). This is the BEST option for real projects.

**Powered by:** Microsoft Roslyn + MSBuild - loads your entire project just like Visual Studio does.

**Parameters:**
- `projectPath` (required): Full path to .csproj file
- `code` (required): C# source code to analyze (can be incomplete)
- `line` (required): Line number (0-based)
- `column` (required): Column position (0-based)
- `maxResults` (optional): Max completions (default 50)

**Example 1: ASP.NET Core Controller**
```json
{
  "projectPath": "/Users/me/WebApi/WebApi.csproj",
  "code": "using Microsoft.AspNetCore.Mvc;\npublic class UserController : ControllerBase { this.",
  "line": 1,
  "column": 53
}
```

**Output:**
```
IntelliSense completions at line 1, column 53
Project: WebApi.csproj
References loaded: 247

Ok (Method)
BadRequest (Method)
NotFound (Method)
Unauthorized (Method)
ModelState (Property)
User (Property)
HttpContext (Property)
... and 142 more
```

**Example 2: Entity Framework DbContext**
```json
{
  "projectPath": "/Users/me/MyApp/MyApp.csproj",
  "code": "var db = new ApplicationDbContext();\ndb.",
  "line": 1,
  "column": 3
}
```

Shows all your DbSet properties and DbContext methods.

**Example 3: Your Custom Classes**
```json
{
  "projectPath": "/Users/me/MyApp/MyApp.csproj",
  "code": "var service = new UserService();\nservice.",
  "line": 1,
  "column": 8
}
```

Shows all methods from your UserService class with full type information.

**Advantages:**
- ✅ **All NuGet packages** - ASP.NET, EF Core, everything in your .csproj
- ✅ **Project references** - Other projects in your solution
- ✅ **Your custom types** - All your classes, interfaces, enums
- ✅ **Correct using statements** - Knows what namespaces are available
- ✅ **Extension methods** - LINQ and custom extensions work
- ✅ **Generic types** - Full support for generics and type parameters

**First call:** May be slow (1-3 seconds) while loading project  
**Subsequent calls:** Much faster with same project

---

### 7. GetIntellisense - Simple Roslyn IntelliSense
**Purpose:** Get IntelliSense-style code completion for simple C# snippets without loading a full project.

**When to use:** For quick code snippets or when you don't have a .csproj file. For real projects, use `GetProjectIntellisense` instead.

**Powered by:** Microsoft Roslyn - the same compiler platform that powers Visual Studio and VS Code C# features.

**Parameters:**
- `code` (required): The C# source code to analyze (can be partial/incomplete)
- `line` (required): Line number where cursor is (0-based, first line is 0)
- `column` (required): Column position in line (0-based, 0 is start)
- `references` (optional): Array of DLL paths for additional type information
- `maxResults` (optional): Max completions to return (default 50)

**Example 1: List methods on a type**
```json
{
  "code": "var list = new List<string>();\nlist.",
  "line": 1,
  "column": 5,
  "maxResults": 50
}
```

**Output:**
```
Completions at line 1, column 5:

Add (Method)
Remove (Method)
Clear (Method)
Contains (Method)
Count (Property)
IndexOf (Method)
Insert (Method)
RemoveAt (Method)
... and 42 more suggestions
```

**Example 2: Type completion**
```json
{
  "code": "using System;\nCon",
  "line": 1,
  "column": 3,
  "maxResults": 20
}
```

**Output:**
```
Completions at line 1, column 3:

Console (Class)
Convert (Class)
ConsoleCancelEventArgs (Class)
ConsoleCancelEventHandler (Delegate)
ConsoleColor (Enum)
...
```

**Example 3: With custom assemblies**
```json
{
  "code": "using MyApp.Services;\nvar service = new UserService();\nservice.",
  "line": 2,
  "column": 8,
  "references": ["/Users/me/MyApp/bin/Debug/MyApp.dll"],
  "maxResults": 50
}
```

**How line and column work:**
- Lines are 0-indexed: line 0 is the first line, line 1 is the second line
- Columns are 0-indexed: column 0 is the start of the line
- If your code is `"Console."` then column 8 is right after the dot
- The tool will suggest completions at that exact position

**What you get:**
- Method names with "(Method)" tag
- Property names with "(Property)" tag
- Class names with "(Class)" tag
- Enum names with "(Enum)" tag
- Other completion types as tagged by Roslyn

---

## Installation

Add to your MCP configuration (`.kiro/settings/mcp.json` or `~/.kiro/settings/mcp.json`):

```json
{
  "mcpServers": {
    "dotmcp-assembly": {
      "command": "dotnet",
      "args": ["run", "--project", "/Users/arczewski/.dot/DotMcp.Assembly/DotMcp.Assembly.csproj"],
      "disabled": false
    }
  }
}
```

## Building

```bash
cd /Users/arczewski/.dot/DotMcp.Assembly
dotnet build
```

## Key Features

✅ **Assembly Inspection** - Browse types, methods, properties in compiled DLLs  
✅ **Smart Search** - Find types across multiple assemblies with wildcards  
✅ **Type Details** - Get detailed metadata about specific classes  
✅ **Member Listing** - List all methods or properties on a type  
✅ **Project-Aware IntelliSense** ⭐ - Full project context with all NuGet packages and references  
✅ **Simple IntelliSense** - Quick completions for code snippets  
✅ **Roslyn-Powered** - Real IDE-quality completions using Microsoft's compiler platform  
✅ **Simple Descriptions** - Optimized for low-parameter AI models with clear, explicit documentation  

## Tool Comparison

**When to use what:**

| Scenario | Tool to Use |
|----------|-------------|
| I have a .csproj and want completions with my project's libraries | `GetProjectIntellisense` ⭐ BEST |
| I want quick completions for a simple code snippet | `GetIntellisense` |
| I want to see what's in a DLL file | `BrowseAssembly` |
| I know the exact class name and want details | `FindTypeInAssembly` |
| I want to see methods on a specific class | `GetMethodsOnType` |
| I want to see properties on a specific class | `GetPropertiesOnType` |
| I don't know which DLL has my class | `SearchTypesInDirectory` |  

## Dependencies

- .NET 10.0
- Microsoft.CodeAnalysis.CSharp.Features (Roslyn)
- ICSharpCode.Decompiler
- System.Reflection.MetadataLoadContext

## Use Cases

1. **Real Project Development** ⭐ - Get accurate completions with all your NuGet packages using `GetProjectIntellisense`
2. **Exploring unfamiliar .NET libraries** - See what's available without opening IDE
3. **AI code generation** - Get accurate completion suggestions for C# code
4. **API discovery** - Find what methods/properties are available on types
5. **Assembly documentation** - Generate quick reference for .NET assemblies
6. **Type searching** - Locate classes across multiple DLLs in a project
7. **Custom class inspection** - See methods/properties on your own classes from compiled DLLs
8. **ASP.NET Core development** - Get completions for controllers, middleware, services with full framework context
9. **Entity Framework** - IntelliSense for DbContext, DbSet, LINQ queries with EF Core packages loaded

## Roslyn Integration Benefits

The `GetIntellisense` tool uses the same Roslyn compiler that powers:
- Visual Studio
- Visual Studio Code with C# extension
- OmniSharp
- Rider (JetBrains)

This means you get:
- **Accurate completions** based on actual C# language semantics
- **Context-aware suggestions** that understand scope and type information
- **Full language support** including LINQ, async/await, generics, etc.
- **Extension method support** automatically included
- **Type inference** for var declarations and lambdas

## Notes

- All paths must be absolute (full paths from root)
- Wildcards use `*` for any characters, `?` for single character
- Line and column numbers are 0-indexed (start at 0)
- For IntelliSense, code can be incomplete/partial - Roslyn handles syntax errors gracefully
- The tool loads .NET runtime assemblies automatically for basic types

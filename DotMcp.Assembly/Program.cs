using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

// ═══════════════════════════════════════════════════════════════════
// Roslyn-based type resolution engine
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Cached Roslyn compilation per directory. All type inspection goes through this.
/// </summary>
static class RoslynEngine
{
    private static readonly ConcurrentDictionary<string, CSharpCompilation> _cache = new();

    /// <summary>
    /// Gets or creates a CSharpCompilation for the given directory.
    /// Loads runtime DLLs + all DLLs in targetDir as metadata references.
    /// </summary>
    public static CSharpCompilation GetCompilation(string targetDir)
    {
        return _cache.GetOrAdd(targetDir, dir =>
        {
            var refs = new List<MetadataReference>();
            var runtimePath = RuntimeEnvironment.GetRuntimeDirectory();

            foreach (var dll in Directory.GetFiles(runtimePath, "*.dll"))
            {
                try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch { }
            }
            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                try { refs.Add(MetadataReference.CreateFromFile(dll)); } catch { }
            }

            return CSharpCompilation.Create("Inspector", references: refs);
        });
    }

    /// <summary>
    /// Gets the IAssemblySymbol for a specific DLL within the compilation.
    /// </summary>
    public static IAssemblySymbol? GetAssemblySymbol(CSharpCompilation compilation, string dllPath)
    {
        var fullPath = Path.GetFullPath(dllPath);
        foreach (var reference in compilation.References)
        {
            if (reference is PortableExecutableReference peRef && 
                string.Equals(Path.GetFullPath(peRef.FilePath ?? ""), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
            }
        }
        return null;
    }

    /// <summary>
    /// Collects all public types from an assembly symbol.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> GetPublicTypes(IAssemblySymbol assembly)
    {
        return GetTypesFromNamespace(assembly.GlobalNamespace);
    }

    private static IEnumerable<INamedTypeSymbol> GetTypesFromNamespace(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.DeclaredAccessibility == Accessibility.Public)
                yield return type;
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in GetTypesFromNamespace(child))
                yield return type;
        }
    }

    /// <summary>
    /// Resolves a type by name with fuzzy matching across an assembly.
    /// </summary>
    public static (INamedTypeSymbol? Type, string? Hint) ResolveType(IAssemblySymbol assembly, string typeName)
    {
        var types = GetPublicTypes(assembly).ToArray();

        // 1. Exact match on simple name or full name
        var exact = types.FirstOrDefault(t => 
            t.Name == typeName || t.ToDisplayString() == typeName);
        if (exact != null) return (exact, null);

        // 2. Ends-with match for partial namespace
        var fullName = typeName.Contains('.') ? typeName : null;
        if (fullName != null)
        {
            var endsWith = types.Where(t => t.ToDisplayString().EndsWith("." + typeName)).ToArray();
            if (endsWith.Length == 1)
                return (endsWith[0], $"[Resolved '{typeName}' → '{endsWith[0].ToDisplayString()}']");
        }

        // 3. Case-insensitive contains match
        var contains = types.Where(t =>
            t.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase) ||
            t.ToDisplayString().Contains(typeName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (contains.Length == 1)
            return (contains[0], $"[Resolved '{typeName}' → '{contains[0].ToDisplayString()}']");

        // 4. Multiple matches — suggest
        if (contains.Length > 1)
        {
            var candidates = contains.Take(5).Select(t => t.ToDisplayString()).ToArray();
            return (null, $"Multiple types match '{typeName}': {string.Join(", ", candidates)}. Use the full type name.");
        }

        return (null, null);
    }

    /// <summary>
    /// Resolves a type across ALL assemblies in the compilation (for FindTypeInProject).
    /// </summary>
    public static (INamedTypeSymbol? Type, string? AssemblyName, string? Hint) ResolveTypeAcrossAssemblies(
        CSharpCompilation compilation, string typeName)
    {
        var isWildcard = typeName.Contains('*') || typeName.Contains('?');
        var results = new List<(INamedTypeSymbol Type, string Assembly)>();

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) continue;
            var asmName = asm.Name;
            if (asmName.StartsWith("System.Private")) continue; // skip internals

            foreach (var type in GetPublicTypes(asm))
            {
                bool matches;
                if (isWildcard)
                    matches = MatchesWildcard(type.Name, typeName) || MatchesWildcard(type.ToDisplayString(), typeName);
                else
                    matches = type.Name == typeName || type.ToDisplayString() == typeName ||
                              type.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase);

                if (matches)
                    results.Add((type, asmName));
            }

            if (results.Count > 50) break;
        }

        if (results.Count == 1) return (results[0].Type, results[0].Assembly, null);
        if (results.Count == 0) return (null, null, null);
        return (results[0].Type, results[0].Assembly, 
            $"Found {results.Count} matches. Showing first.");
    }

    // ─── Formatting ───

    private static readonly SymbolDisplayFormat _typeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    private static readonly SymbolDisplayFormat _memberFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
                       SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string FormatType(ITypeSymbol type) => type.ToDisplayString(_typeFormat);

    public static string FormatMember(ISymbol member) => member.ToDisplayString(_memberFormat);

    public static string FormatMethodSignature(IMethodSymbol method)
    {
        var sb = new StringBuilder();
        if (method.DeclaredAccessibility == Accessibility.Public) sb.Append("public ");
        if (method.IsStatic) sb.Append("static ");
        if (method.IsVirtual) sb.Append("virtual ");
        if (method.IsAbstract) sb.Append("abstract ");
        if (method.IsOverride) sb.Append("override ");

        sb.Append(FormatType(method.ReturnType));
        sb.Append(' ');
        sb.Append(method.Name);

        if (method.TypeParameters.Length > 0)
            sb.Append($"<{string.Join(", ", method.TypeParameters.Select(tp => tp.Name))}>");

        sb.Append('(');
        sb.Append(string.Join(", ", method.Parameters.Select(p =>
            $"{FormatType(p.Type)} {p.Name}")));
        sb.Append(')');

        return sb.ToString();
    }

    public static string FormatConstructor(IMethodSymbol ctor, string typeName)
    {
        var sb = new StringBuilder();
        sb.Append("public ");
        sb.Append(typeName);
        sb.Append('(');
        sb.Append(string.Join(", ", ctor.Parameters.Select(p =>
            $"{FormatType(p.Type)} {p.Name}")));
        sb.Append(')');
        return sb.ToString();
    }

    public static string FormatProperty(IPropertySymbol prop)
    {
        var isStatic = prop.IsStatic ? "static " : "";
        var accessor = prop.GetMethod != null && prop.SetMethod != null ? "{ get; set; }" :
                      prop.GetMethod != null ? "{ get; }" : "{ set; }";
        return $"  {isStatic}{FormatType(prop.Type)} {prop.Name} {accessor}";
    }

    public static string FormatField(IFieldSymbol field)
    {
        var mods = new List<string>();
        if (field.IsStatic) mods.Add("static");
        if (field.IsReadOnly) mods.Add("readonly");
        if (field.IsConst) mods.Add("const");
        var modStr = mods.Count > 0 ? string.Join(" ", mods) + " " : "";
        return $"  {modStr}{FormatType(field.Type)} {field.Name}";
    }

    /// <summary>
    /// Detects collection/enumerable inheritance and returns a helpful note.
    /// </summary>
    public static string? GetCollectionInfo(INamedTypeSymbol type)
    {
        var baseType = type.BaseType;
        while (baseType != null && baseType.SpecialType != SpecialType.System_Object)
        {
            var name = baseType.ToDisplayString();
            if (name.Contains("Collection") || name.Contains("List") || name.Contains("Dictionary"))
                return $"Inherits {FormatType(baseType)} — use foreach/LINQ directly (.ToList(), .Where(), .Select()).";
            baseType = baseType.BaseType;
        }

        var enumerable = type.AllInterfaces.FirstOrDefault(i =>
            i.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>");
        if (enumerable != null)
        {
            var elementType = enumerable.TypeArguments.FirstOrDefault();
            if (elementType != null)
                return $"Implements IEnumerable<{FormatType(elementType)}> — iterable with foreach/LINQ. No .Items/.Data/.Models property needed.";
        }
        return null;
    }

    // ─── Helpers ───

    public static bool MatchesWildcard(string input, string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    public static string? ValidatePath(string path)
    {
        if (!File.Exists(path))
            return $"Error: File not found: '{path}'";
        if (path.Contains(".nuget/packages") || path.Contains(".nuget\\packages"))
            return $"Error: Path is inside the NuGet package cache. " +
                   "Run 'dotnet build' first, then use the DLL from bin/Debug/<tfm>/ where all dependencies are copied.";
        return null;
    }
}

// ═══════════════════════════════════════════════════════════════════
// MCP Tool definitions
// ═══════════════════════════════════════════════════════════════════

[McpServerToolType]
public static class AssemblyTools
{
    private static readonly HashSet<string> ObjectMethods = ["ToString", "Equals", "GetHashCode", "GetType"];

    [McpServerTool, Description(
        "USAGE: Show all classes, methods, and properties inside a .NET DLL or EXE file. " +
        "WHEN TO USE: When you want to explore what's inside a compiled assembly and don't know the exact type name. " +
        "PREFER GetTypeSummary: If you already know the class name, use GetTypeSummary for complete info in one call. " +
        "REQUIRED: 'path' - full absolute path to DLL or EXE file. " +
        "OPTIONAL: typeNameFilter (wildcard), maxTypes (default 100), maxMembersPerType (default 20). " +
        "OUTPUT: Returns text with class names and their public methods/properties with FULL namespaces on all types.")]
    public static string BrowseAssembly(
        [Description("REQUIRED: Full absolute path to the .dll or .exe file.")] string path,
        [Description("OPTIONAL: Wildcard filter. Examples: '*Controller', '*Service*'.")] string? typeNameFilter = null,
        [Description("OPTIONAL: Max classes to show. Default 100, -1 for all.")] int maxTypes = 100,
        [Description("OPTIONAL: Max members per class. Default 20, -1 for all.")] int maxMembersPerType = 20,
        [Description("OPTIONAL: Hide types starting with 'Internal'. Default true.")] bool excludeInternalTypes = true,
        [Description("OPTIONAL: Hide ToString/Equals/GetHashCode/GetType. Default true.")] bool hideObjectMembers = true)
    {
        var error = RoslynEngine.ValidatePath(path);
        if (error != null) return error;

        try
        {
            var targetDir = Path.GetDirectoryName(path)!;
            var compilation = RoslynEngine.GetCompilation(targetDir);
            var assembly = RoslynEngine.GetAssemblySymbol(compilation, path);
            if (assembly == null) return $"Error: Could not load assembly '{Path.GetFileName(path)}' into compilation.";

            var sb = new StringBuilder();
            int typesListed = 0;

            foreach (var type in RoslynEngine.GetPublicTypes(assembly))
            {
                if (maxTypes >= 0 && typesListed >= maxTypes) break;
                if (excludeInternalTypes && type.Name.StartsWith("Internal")) continue;
                if (typeNameFilter != null && !RoslynEngine.MatchesWildcard(type.Name, typeNameFilter)) continue;

                sb.AppendLine(type.ToDisplayString());
                if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
                    sb.AppendLine($"  : {RoslynEngine.FormatType(type.BaseType)}");
                typesListed++;

                int membersListed = 0;
                foreach (var member in type.GetMembers())
                {
                    if (maxMembersPerType >= 0 && membersListed >= maxMembersPerType) break;
                    if (member.DeclaredAccessibility != Accessibility.Public) continue;
                    if (member.IsImplicitlyDeclared) continue;

                    switch (member)
                    {
                        case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
                            if (hideObjectMembers && ObjectMethods.Contains(m.Name)) continue;
                            var parms = string.Join(", ", m.Parameters.Select(p => $"{RoslynEngine.FormatType(p.Type)} {p.Name}"));
                            sb.AppendLine($"  {RoslynEngine.FormatType(m.ReturnType)} {m.Name}({parms})");
                            membersListed++;
                            break;
                        case IPropertySymbol p:
                            sb.AppendLine($"  {RoslynEngine.FormatType(p.Type)} {p.Name}");
                            membersListed++;
                            break;
                    }
                }
            }

            return sb.Length > 0 ? sb.ToString() : "No types matched the filter or assembly is empty.";
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Get a COMPLETE summary of a type in a single call: inheritance, interfaces, constructors, properties, fields, and methods with FULL NAMESPACES. " +
        "This is the FASTEST way to learn about a type — use this FIRST before writing code that uses a type. " +
        "EXAMPLE: path='./bin/Debug/net10.0/OpenAI.dll', typeName='ChatClient'. " +
        "REQUIRED: 'path' (DLL path) and 'typeName' (supports fuzzy matching). " +
        "OUTPUT: Complete type info with fully-qualified type names on all parameters and return types.")]
    public static string GetTypeSummary(
        [Description("REQUIRED: Full path to the .dll or .exe file.")] string path,
        [Description("REQUIRED: Class name. Supports fuzzy matching.")] string typeName,
        [Description("OPTIONAL: Include inherited members. Default false.")] bool includeInherited = false,
        [Description("OPTIONAL: Max methods to show. Default 30, -1 for all.")] int maxMethods = 30)
    {
        var error = RoslynEngine.ValidatePath(path);
        if (error != null) return error;

        try
        {
            var targetDir = Path.GetDirectoryName(path)!;
            var compilation = RoslynEngine.GetCompilation(targetDir);
            var assembly = RoslynEngine.GetAssemblySymbol(compilation, path);
            if (assembly == null) return $"Error: Could not load assembly '{Path.GetFileName(path)}'.";

            var (type, hint) = RoslynEngine.ResolveType(assembly, typeName);
            if (type == null) return hint ?? $"Type '{typeName}' not found in assembly.";

            var sb = new StringBuilder();
            if (hint != null) sb.AppendLine(hint);

            // Header
            sb.AppendLine($"═══ {type.ToDisplayString()} ═══");
            if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
                sb.AppendLine($"Base: {RoslynEngine.FormatType(type.BaseType)}");
            if (type.Interfaces.Length > 0)
                sb.AppendLine($"Interfaces: {string.Join(", ", type.Interfaces.Select(i => RoslynEngine.FormatType(i)))}");

            var collInfo = RoslynEngine.GetCollectionInfo(type);
            if (collInfo != null) sb.AppendLine(collInfo);

            sb.AppendLine($"Abstract: {type.IsAbstract} | Sealed: {type.IsSealed} | Generic: {type.IsGenericType}");
            sb.AppendLine();

            // Constructors
            var ctors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
            if (ctors.Count > 0)
            {
                sb.AppendLine("── Constructors ──");
                foreach (var ctor in ctors)
                    sb.AppendLine($"  {RoslynEngine.FormatConstructor(ctor, type.Name)}");

                // Factory hint: look for methods in other types that return this type
                var factories = RoslynEngine.GetPublicTypes(assembly)
                    .Where(t => !SymbolEqualityComparer.Default.Equals(t, type))
                    .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>()
                        .Where(m => m.DeclaredAccessibility == Accessibility.Public &&
                                    SymbolEqualityComparer.Default.Equals(m.ReturnType, type))
                        .Select(m => $"{t.Name}.{m.Name}()"))
                    .Take(3).ToList();
                if (factories.Count > 0)
                    sb.AppendLine($"  [Factory: {string.Join(", ", factories)} — prefer to inherit endpoint/auth config]");

                sb.AppendLine();
            }

            // Properties
            var props = GetMembers<IPropertySymbol>(type, includeInherited)
                .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsImplicitlyDeclared).ToList();
            if (props.Count > 0)
            {
                sb.AppendLine("── Properties ──");
                foreach (var p in props)
                    sb.AppendLine(RoslynEngine.FormatProperty(p));
                sb.AppendLine();
            }

            // Fields
            var fields = GetMembers<IFieldSymbol>(type, includeInherited)
                .Where(f => f.DeclaredAccessibility == Accessibility.Public && !f.IsImplicitlyDeclared).ToList();
            if (fields.Count > 0)
            {
                sb.AppendLine("── Fields ──");
                foreach (var f in fields)
                    sb.AppendLine(RoslynEngine.FormatField(f));
                sb.AppendLine();
            }

            // Methods
            var methods = GetMembers<IMethodSymbol>(type, includeInherited)
                .Where(m => m.MethodKind == MethodKind.Ordinary &&
                            m.DeclaredAccessibility == Accessibility.Public &&
                            !ObjectMethods.Contains(m.Name) && !m.IsImplicitlyDeclared)
                .Take(maxMethods > 0 ? maxMethods : int.MaxValue).ToList();
            if (methods.Count > 0)
            {
                sb.AppendLine("── Methods ──");
                foreach (var m in methods)
                    sb.AppendLine($"  {RoslynEngine.FormatMethodSignature(m)}");
            }
            else if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
            {
                sb.AppendLine("── Methods ──");
                sb.AppendLine($"  [No declared public methods — inherits from {RoslynEngine.FormatType(type.BaseType)}. Use LINQ/foreach directly.]");
            }

            return sb.ToString();
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Get all methods on a specific class with full type signatures. " +
        "Shows exact method names, full generic return types, and parameter signatures with FULL NAMESPACES. " +
        "REQUIRED: 'path' (DLL path) and 'typeName' (class name, supports fuzzy matching). " +
        "OUTPUT: Method signatures like: public virtual Task<System.ClientModel.ClientResult<OpenAI.Models.OpenAIModelCollection>> GetModelsAsync(System.Threading.CancellationToken cancellationToken)")]
    public static string GetMethodsOnType(
        [Description("REQUIRED: Full path to the .dll or .exe file.")] string path,
        [Description("REQUIRED: Class name. Supports fuzzy matching.")] string typeName,
        [Description("OPTIONAL: Include inherited methods. Default false.")] bool includeInherited = false,
        [Description("OPTIONAL: Max methods. Default 50, -1 for all.")] int maxMethods = 50,
        [Description("OPTIONAL: Wildcard filter on method name. Example: 'Get*', '*Async'.")] string? memberNameFilter = null,
        [Description("OPTIONAL: Hide Object methods. Default true.")] bool hideObjectMembers = true)
    {
        var error = RoslynEngine.ValidatePath(path);
        if (error != null) return error;

        try
        {
            var targetDir = Path.GetDirectoryName(path)!;
            var compilation = RoslynEngine.GetCompilation(targetDir);
            var assembly = RoslynEngine.GetAssemblySymbol(compilation, path);
            if (assembly == null) return $"Error: Could not load assembly '{Path.GetFileName(path)}'.";

            var (type, hint) = RoslynEngine.ResolveType(assembly, typeName);
            if (type == null) return hint ?? $"Type '{typeName}' not found in assembly.";

            var sb = new StringBuilder();
            if (hint != null) sb.AppendLine(hint);
            sb.AppendLine($"Methods on type: {type.ToDisplayString()}");
            if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
                sb.AppendLine($"Base: {RoslynEngine.FormatType(type.BaseType)}");
            sb.AppendLine();

            // Constructors
            var ctors = type.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).ToList();
            if (ctors.Count > 0)
            {
                foreach (var ctor in ctors)
                    sb.AppendLine(RoslynEngine.FormatConstructor(ctor, type.Name));

                // Factory methods hint
                var factories = RoslynEngine.GetPublicTypes(assembly)
                    .Where(t => !SymbolEqualityComparer.Default.Equals(t, type))
                    .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>()
                        .Where(m => m.DeclaredAccessibility == Accessibility.Public &&
                                    SymbolEqualityComparer.Default.Equals(m.ReturnType, type))
                        .Select(m => $"{t.Name}.{m.Name}()"))
                    .Take(3).ToList();
                if (factories.Count > 0)
                    sb.AppendLine($"[Also obtainable via: {string.Join(", ", factories)} — prefer factory to inherit endpoint/auth config]");
                sb.AppendLine();
            }

            // Methods
            var methods = GetMembers<IMethodSymbol>(type, includeInherited)
                .Where(m => m.MethodKind == MethodKind.Ordinary &&
                            m.DeclaredAccessibility == Accessibility.Public &&
                            !m.IsImplicitlyDeclared)
                .Where(m => !hideObjectMembers || !ObjectMethods.Contains(m.Name))
                .Where(m => memberNameFilter == null || RoslynEngine.MatchesWildcard(m.Name, memberNameFilter))
                .Take(maxMethods > 0 ? maxMethods : int.MaxValue);

            foreach (var m in methods)
                sb.AppendLine(RoslynEngine.FormatMethodSignature(m));

            var result = sb.ToString();
            return result.Trim().Length > 0 ? result : "No methods found.";
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Get all properties and fields on a specific class with full type names. " +
        "REQUIRED: 'path' (DLL path) and 'typeName' (class name). " +
        "OUTPUT: Properties with get/set info and fields with modifiers, all types fully namespace-qualified.")]
    public static string GetPropertiesOnType(
        [Description("REQUIRED: Full path to the .dll or .exe file.")] string path,
        [Description("REQUIRED: Class name. Supports fuzzy matching.")] string typeName,
        [Description("OPTIONAL: Include inherited members. Default false.")] bool includeInherited = false)
    {
        var error = RoslynEngine.ValidatePath(path);
        if (error != null) return error;

        try
        {
            var targetDir = Path.GetDirectoryName(path)!;
            var compilation = RoslynEngine.GetCompilation(targetDir);
            var assembly = RoslynEngine.GetAssemblySymbol(compilation, path);
            if (assembly == null) return $"Error: Could not load assembly '{Path.GetFileName(path)}'.";

            var (type, hint) = RoslynEngine.ResolveType(assembly, typeName);
            if (type == null) return hint ?? $"Type '{typeName}' not found in assembly.";

            var sb = new StringBuilder();
            if (hint != null) sb.AppendLine(hint);
            sb.AppendLine($"Properties and Fields on type: {type.ToDisplayString()}");

            var collInfo = RoslynEngine.GetCollectionInfo(type);
            if (collInfo != null) sb.AppendLine(collInfo);
            sb.AppendLine();

            var props = GetMembers<IPropertySymbol>(type, includeInherited)
                .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsImplicitlyDeclared).ToList();
            if (props.Count > 0)
            {
                sb.AppendLine("Properties:");
                foreach (var p in props)
                    sb.AppendLine(RoslynEngine.FormatProperty(p));
                sb.AppendLine();
            }

            var fields = GetMembers<IFieldSymbol>(type, includeInherited)
                .Where(f => f.DeclaredAccessibility == Accessibility.Public && !f.IsImplicitlyDeclared).ToList();
            if (fields.Count > 0)
            {
                sb.AppendLine("Fields:");
                foreach (var f in fields)
                    sb.AppendLine(RoslynEngine.FormatField(f));
            }

            return sb.Length > 30 ? sb.ToString() : "No properties or fields found.";
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Find one specific class and show its details (namespace, base class, interfaces, member counts). " +
        "REQUIRED: 'path' (DLL path) and 'typeName' (supports fuzzy matching). " +
        "Use GetTypeSummary instead if you want to see the actual members.")]
    public static string FindTypeInAssembly(
        [Description("REQUIRED: Full path to the .dll or .exe file.")] string path,
        [Description("REQUIRED: Class name to find.")] string typeName)
    {
        var error = RoslynEngine.ValidatePath(path);
        if (error != null) return error;

        try
        {
            var targetDir = Path.GetDirectoryName(path)!;
            var compilation = RoslynEngine.GetCompilation(targetDir);
            var assembly = RoslynEngine.GetAssemblySymbol(compilation, path);
            if (assembly == null) return $"Error: Could not load assembly '{Path.GetFileName(path)}'.";

            var (type, hint) = RoslynEngine.ResolveType(assembly, typeName);
            if (type == null) return hint ?? $"Type '{typeName}' not found in assembly.";

            var sb = new StringBuilder();
            if (hint != null) sb.AppendLine(hint);
            sb.AppendLine($"Type: {type.ToDisplayString()}");
            sb.AppendLine($"Namespace: {type.ContainingNamespace.ToDisplayString()}");
            sb.AppendLine($"Assembly: {type.ContainingAssembly.Name}");
            if (type.BaseType != null && type.BaseType.SpecialType != SpecialType.System_Object)
                sb.AppendLine($"Base Type: {RoslynEngine.FormatType(type.BaseType)}");
            if (type.Interfaces.Length > 0)
                sb.AppendLine($"Interfaces: {string.Join(", ", type.Interfaces.Select(i => RoslynEngine.FormatType(i)))}");
            sb.AppendLine($"Abstract: {type.IsAbstract} | Sealed: {type.IsSealed} | Generic: {type.IsGenericType}");
            var memberCounts = type.GetMembers().Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsImplicitlyDeclared);
            sb.AppendLine($"Methods: {memberCounts.OfType<IMethodSymbol>().Count(m => m.MethodKind == MethodKind.Ordinary)}");
            sb.AppendLine($"Properties: {memberCounts.OfType<IPropertySymbol>().Count()}");
            sb.AppendLine($"Fields: {memberCounts.OfType<IFieldSymbol>().Count()}");

            return sb.ToString();
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Search for types matching a wildcard pattern in a SINGLE DLL. " +
        "REQUIRED: 'path' (DLL path) and 'typePattern' (wildcard like '*Client', '*Service*'). " +
        "OUTPUT: List of matching full type names.")]
    public static string SearchTypesInAssembly(
        [Description("REQUIRED: Full path to the .dll or .exe file.")] string path,
        [Description("REQUIRED: Wildcard pattern. Examples: '*Client', '*Service*'.")] string typePattern,
        [Description("OPTIONAL: Max results. Default 50.")] int maxResults = 50)
    {
        var error = RoslynEngine.ValidatePath(path);
        if (error != null) return error;

        try
        {
            var targetDir = Path.GetDirectoryName(path)!;
            var compilation = RoslynEngine.GetCompilation(targetDir);
            var assembly = RoslynEngine.GetAssemblySymbol(compilation, path);
            if (assembly == null) return $"Error: Could not load assembly '{Path.GetFileName(path)}'.";

            var sb = new StringBuilder();
            int count = 0;
            foreach (var type in RoslynEngine.GetPublicTypes(assembly))
            {
                if (count >= maxResults) break;
                if (RoslynEngine.MatchesWildcard(type.Name, typePattern) ||
                    RoslynEngine.MatchesWildcard(type.ToDisplayString(), typePattern))
                {
                    sb.AppendLine(type.ToDisplayString());
                    count++;
                }
            }

            return count == 0
                ? $"No types matching '{typePattern}' found in {Path.GetFileName(path)}."
                : $"Found {count} type(s) matching '{typePattern}' in {Path.GetFileName(path)}:\n{sb}";
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Search for types across ALL DLLs in a directory. " +
        "WHEN TO USE: When you don't know which DLL contains the class. " +
        "REQUIRED: 'directoryPath' (folder with DLLs) and 'typePattern' (wildcard). " +
        "OUTPUT: Type names with which DLL they're in.")]
    public static string SearchTypesInDirectory(
        [Description("REQUIRED: Directory containing .dll files.")] string directoryPath,
        [Description("REQUIRED: Wildcard pattern like '*Controller', '*Service*'.")] string typePattern,
        [Description("OPTIONAL: Max results. Default 50.")] int maxResults = 50)
    {
        try
        {
            if (!Directory.Exists(directoryPath))
                return $"Error: Directory not found: '{directoryPath}'";

            var compilation = RoslynEngine.GetCompilation(directoryPath);
            var sb = new StringBuilder();
            int count = 0;

            foreach (var reference in compilation.References)
            {
                if (count >= maxResults) break;
                if (reference is not PortableExecutableReference peRef) continue;
                var refPath = peRef.FilePath;
                if (refPath == null || !refPath.StartsWith(Path.GetFullPath(directoryPath))) continue;

                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) continue;

                foreach (var type in RoslynEngine.GetPublicTypes(asm))
                {
                    if (count >= maxResults) break;
                    if (RoslynEngine.MatchesWildcard(type.Name, typePattern) ||
                        RoslynEngine.MatchesWildcard(type.ToDisplayString(), typePattern))
                    {
                        sb.AppendLine($"{type.ToDisplayString()} (in {Path.GetFileName(refPath)})");
                        count++;
                    }
                }
            }

            return sb.Length > 0 ? sb.ToString() : $"No types matching '{typePattern}' found.";
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Find which DLL contains a specific type by scanning a project's build output (bin/Debug + bin/Release). " +
        "WHEN TO USE: When you know a type name but don't know which assembly it lives in. " +
        "PREREQUISITE: Project must be built first ('dotnet build'). " +
        "REQUIRED: 'projectPath' (.csproj path) and 'typeName' (exact or wildcard).")]
    public static string FindTypeInProject(
        [Description("REQUIRED: Path to the .csproj file.")] string projectPath,
        [Description("REQUIRED: Type name (exact or wildcard like '*Credential*').")] string typeName,
        [Description("OPTIONAL: Max results. Default 20.")] int maxResults = 20)
    {
        try
        {
            if (!File.Exists(projectPath))
                return $"Error: Project file not found: '{projectPath}'";

            var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
            var binDir = Path.Combine(projectDir, "bin");
            if (!Directory.Exists(binDir))
                return "Error: No 'bin' folder found. Run 'dotnet build' first.";

            // Find the most recent output directory
            var outputDirs = new List<string>();
            foreach (var config in new[] { "Debug", "Release" })
            {
                var configDir = Path.Combine(binDir, config);
                if (!Directory.Exists(configDir)) continue;
                foreach (var tfmDir in Directory.GetDirectories(configDir))
                {
                    if (Directory.GetFiles(tfmDir, "*.dll").Length > 0)
                        outputDirs.Add(tfmDir);
                }
            }

            if (outputDirs.Count == 0)
                return "Error: No build output found. Run 'dotnet build' first.";

            var targetDir = outputDirs
                .OrderByDescending(d => Directory.GetFiles(d, "*.dll").Max(f => File.GetLastWriteTimeUtc(f)))
                .First();

            var compilation = RoslynEngine.GetCompilation(targetDir);
            var isWildcard = typeName.Contains('*') || typeName.Contains('?');
            var sb = new StringBuilder();
            sb.AppendLine($"Searching in: {Path.GetRelativePath(projectDir, targetDir)}");
            sb.AppendLine();

            int count = 0;
            foreach (var reference in compilation.References)
            {
                if (count >= maxResults) break;
                if (reference is not PortableExecutableReference peRef) continue;
                var refPath = peRef.FilePath;
                if (refPath == null || !refPath.StartsWith(targetDir)) continue;

                if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) continue;

                foreach (var type in RoslynEngine.GetPublicTypes(asm))
                {
                    if (count >= maxResults) break;
                    bool matches = isWildcard
                        ? RoslynEngine.MatchesWildcard(type.Name, typeName) || RoslynEngine.MatchesWildcard(type.ToDisplayString(), typeName)
                        : type.Name == typeName || type.ToDisplayString() == typeName ||
                          type.Name.Contains(typeName, StringComparison.OrdinalIgnoreCase);
                    if (matches)
                    {
                        sb.AppendLine($"{type.ToDisplayString()} (in {Path.GetFileName(refPath)})");
                        count++;
                    }
                }
            }

            return count == 0
                ? $"No types matching '{typeName}' found. Ensure project is built."
                : sb.ToString();
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Get IntelliSense completions for C# code within a REAL PROJECT context. " +
        "Loads ALL project dependencies automatically. " +
        "REQUIRED: projectPath (.csproj), code, line (0-based), column (0-based).")]
    public static async Task<string> GetProjectIntellisense(
        [Description("REQUIRED: Full path to .csproj file.")] string projectPath,
        [Description("REQUIRED: C# source code to analyze.")] string code,
        [Description("REQUIRED: Line number (0-based).")] int line,
        [Description("REQUIRED: Column position (0-based).")] int column,
        [Description("OPTIONAL: Max completions. Default 50.")] int maxResults = 50)
    {
        try
        {
            if (!File.Exists(projectPath))
                return $"Error: Project file not found: '{projectPath}'";

            var workspace = MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(projectPath);

            var sourceText = SourceText.From(code);
            var tempDoc = project.AddDocument("temp.cs", sourceText);
            var text = await tempDoc.GetTextAsync();
            var lines = text.Lines;

            if (line >= lines.Count)
                return $"Error: Line {line} is out of range. Code has {lines.Count} lines (0-indexed).";
            var targetLine = lines[line];
            if (column > targetLine.End - targetLine.Start)
                return $"Error: Column {column} is out of range. Line {line} has {targetLine.End - targetLine.Start} characters.";

            var position = targetLine.Start + column;
            var completionService = CompletionService.GetService(tempDoc);
            if (completionService == null) return "Error: Could not get completion service.";

            var completions = await completionService.GetCompletionsAsync(tempDoc, position);
            if (completions == null || completions.ItemsList.Count == 0)
                return "No completions available at this position.";

            var sb = new StringBuilder();
            sb.AppendLine($"IntelliSense at line {line}, column {column} ({project.MetadataReferences.Count()} refs loaded)");
            sb.AppendLine();

            foreach (var item in completions.ItemsList.Take(maxResults))
            {
                var kind = item.Tags.FirstOrDefault() ?? "Unknown";
                var desc = item.InlineDescription ?? "";
                sb.AppendLine($"{item.DisplayText} ({kind}){(string.IsNullOrEmpty(desc) ? "" : $" - {desc}")}");
            }

            if (completions.ItemsList.Count > maxResults)
                sb.AppendLine($"\n... and {completions.ItemsList.Count - maxResults} more");

            workspace.Dispose();
            return sb.ToString();
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    [McpServerTool, Description(
        "USAGE: Get IntelliSense completions for standalone C# code snippets. " +
        "For real projects with dependencies, use GetProjectIntellisense instead. " +
        "REQUIRED: code, line (0-based), column (0-based).")]
    public static async Task<string> GetIntellisense(
        [Description("REQUIRED: C# source code.")] string code,
        [Description("REQUIRED: Line number (0-based).")] int line,
        [Description("REQUIRED: Column position (0-based).")] int column,
        [Description("OPTIONAL: DLL paths to reference.")] string[]? references = null,
        [Description("OPTIONAL: Max completions. Default 50.")] int maxResults = 50)
    {
        try
        {
            var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
            var workspace = new AdhocWorkspace(host);

            var metaRefs = new List<MetadataReference>();
            var runtimePath = RuntimeEnvironment.GetRuntimeDirectory();
            foreach (var dll in new[] { "System.dll", "System.Core.dll", "System.Runtime.dll", 
                                        "mscorlib.dll", "System.Private.CoreLib.dll",
                                        "System.Collections.dll", "System.Linq.dll", "System.Console.dll" })
            {
                var p = Path.Combine(runtimePath, dll);
                if (File.Exists(p)) metaRefs.Add(MetadataReference.CreateFromFile(p));
            }
            if (references != null)
                foreach (var r in references)
                    if (File.Exists(r)) metaRefs.Add(MetadataReference.CreateFromFile(r));

            var projectInfo = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(),
                "Temp", "Temp", LanguageNames.CSharp, metadataReferences: metaRefs);
            var project = workspace.AddProject(projectInfo);

            var sourceText = SourceText.From(code);
            var docInfo = DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "temp.cs",
                loader: TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())));
            var document = workspace.AddDocument(docInfo);

            var text = await document.GetTextAsync();
            var lines = text.Lines;
            if (line >= lines.Count) return $"Error: Line {line} out of range ({lines.Count} lines).";
            var targetLine = lines[line];
            if (column > targetLine.End - targetLine.Start) return $"Error: Column {column} out of range.";

            var position = targetLine.Start + column;
            var completionService = CompletionService.GetService(document);
            if (completionService == null) return "Error: Could not get completion service.";

            var completions = await completionService.GetCompletionsAsync(document, position);
            if (completions == null || completions.ItemsList.Count == 0) return "No completions at this position.";

            var sb = new StringBuilder();
            sb.AppendLine($"Completions at line {line}, column {column}:");
            sb.AppendLine();
            foreach (var item in completions.ItemsList.Take(maxResults))
            {
                var kind = item.Tags.FirstOrDefault() ?? "Unknown";
                var desc = item.InlineDescription ?? "";
                sb.AppendLine($"{item.DisplayText} ({kind}){(string.IsNullOrEmpty(desc) ? "" : $" - {desc}")}");
            }
            if (completions.ItemsList.Count > maxResults)
                sb.AppendLine($"\n... and {completions.ItemsList.Count - maxResults} more");
            return sb.ToString();
        }
        catch (Exception e) { return $"Error: {e.Message}"; }
    }

    // ─── Helpers ───

    private static IEnumerable<T> GetMembers<T>(INamedTypeSymbol type, bool includeInherited) where T : ISymbol
    {
        if (!includeInherited)
            return type.GetMembers().OfType<T>();

        var members = new List<T>();
        var current = type;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            members.AddRange(current.GetMembers().OfType<T>());
            current = current.BaseType;
        }
        return members;
    }

    [McpServerTool, Description(
        "FIX CS0246 AND CS0234 BUILD ERRORS. When 'dotnet build' fails with 'type or namespace could not be found' (CS0246) or 'does not exist in namespace' (CS0234), " +
        "call this tool to get the exact 'using' statements needed. It compiles the file within the project context, identifies all unresolved types, " +
        "and searches all referenced assemblies to find which namespaces to import. " +
        "REQUIRED: 'projectPath' (.csproj) and 'filePath' (the .cs file with errors). " +
        "OUTPUT: The using directives to add, e.g. 'using System.ClientModel;' 'using OpenAI;'. If file compiles cleanly, says so.")]
    public static async Task<string> FixBuildErrors_FindMissingUsingsAndImports(
        [Description("REQUIRED: Path to the .csproj file.")] string projectPath,
        [Description("REQUIRED: Path to the .cs file that has missing using directives.")] string filePath)
    {
        try
        {
            if (!File.Exists(projectPath))
                return $"Error: Project file not found: '{projectPath}'";
            if (!File.Exists(filePath))
                return $"Error: Source file not found: '{filePath}'";

            var workspace = MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(Path.GetFullPath(projectPath));

            // Find the document in the project
            var fullFilePath = Path.GetFullPath(filePath);
            var document = project.Documents.FirstOrDefault(d =>
                string.Equals(Path.GetFullPath(d.FilePath ?? ""), fullFilePath, StringComparison.OrdinalIgnoreCase));

            if (document == null)
            {
                // File might not be part of the project — add it temporarily
                var sourceText = SourceText.From(File.ReadAllText(fullFilePath));
                document = project.AddDocument(Path.GetFileName(filePath), sourceText);
            }

            var compilation = await document.Project.GetCompilationAsync();
            if (compilation == null)
                return "Error: Could not create compilation for the project.";

            var semanticModel = await document.GetSemanticModelAsync();
            if (semanticModel == null)
                return "Error: Could not get semantic model.";

            // Get diagnostics for missing types (CS0246, CS0234)
            var diagnostics = semanticModel.GetDiagnostics()
                .Where(d => d.Id is "CS0246" or "CS0234")
                .ToList();

            if (diagnostics.Count == 0)
            {
                workspace.Dispose();
                return "No missing imports detected — file compiles cleanly for type resolution.";
            }

            // Extract the unresolved type names from diagnostics
            var syntaxRoot = await document.GetSyntaxTreeAsync();
            var root = syntaxRoot?.GetRoot();
            var unresolvedNames = new HashSet<string>();

            foreach (var diag in diagnostics)
            {
                var span = diag.Location.SourceSpan;
                if (root != null)
                {
                    var node = root.FindNode(span);
                    var text = node.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        unresolvedNames.Add(text);
                }
            }

            // Search for the unresolved types across all referenced assemblies
            var suggestedUsings = new SortedSet<string>();

            foreach (var unresolvedName in unresolvedNames)
            {
                // Check if it's a namespace reference (CS0234) or type reference (CS0246)
                foreach (var reference in compilation.References)
                {
                    if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol asm) continue;

                    // Search for types matching the unresolved name
                    var matchingTypes = FindTypesRecursive(asm.GlobalNamespace, unresolvedName).ToList();
                    foreach (var match in matchingTypes)
                    {
                        var ns = match.ContainingNamespace.ToDisplayString();
                        if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>")
                            suggestedUsings.Add(ns);
                    }
                }
            }

            workspace.Dispose();

            if (suggestedUsings.Count == 0)
            {
                return $"Found {diagnostics.Count} unresolved type(s): {string.Join(", ", unresolvedNames)}\n" +
                       "Could not find matching types in project references. Check spelling or ensure the NuGet package is referenced.";
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Missing using directives for {Path.GetFileName(filePath)}:");
            sb.AppendLine();
            foreach (var ns in suggestedUsings)
                sb.AppendLine($"using {ns};");
            sb.AppendLine();
            sb.AppendLine($"Add these at the top of the file to resolve {diagnostics.Count} error(s) for: {string.Join(", ", unresolvedNames)}");

            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"Error: {e.Message}\n\nTroubleshooting:\n- Ensure project builds: dotnet build \"{projectPath}\"\n- Check paths are correct and file is a .cs file";
        }
    }

    private static IEnumerable<INamedTypeSymbol> FindTypesRecursive(INamespaceSymbol ns, string name)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            if (type.DeclaredAccessibility == Accessibility.Public &&
                (type.Name == name || type.MetadataName == name))
                yield return type;
        }
        foreach (var child in ns.GetNamespaceMembers())
        {
            // Also check if the unresolved name IS a namespace (for CS0234)
            foreach (var type in FindTypesRecursive(child, name))
                yield return type;
        }
    }
}

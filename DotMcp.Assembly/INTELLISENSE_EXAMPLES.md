# IntelliSense Examples

This document shows practical examples of using the Roslyn-powered `GetIntellisense` tool.

## Understanding Line and Column Numbers

```csharp
var list = new List<string>();  // line 0
list.                            // line 1, column 5 is after the dot
```

- Lines start at 0 (first line = line 0)
- Columns start at 0 (first character = column 0)
- Put cursor position where you want completions

## Example 1: Simple List Operations

**Goal:** See what methods are available on `List<string>`

```json
{
  "code": "using System.Collections.Generic;\nvar list = new List<string>();\nlist.",
  "line": 2,
  "column": 5
}
```

**Expected completions:**
- Add
- Remove
- Clear
- Contains
- Count
- IndexOf
- Insert
- RemoveAt
- etc.

## Example 2: Console Class

**Goal:** See what's available on Console

```json
{
  "code": "using System;\nConsole.",
  "line": 1,
  "column": 8
}
```

**Expected completions:**
- WriteLine
- ReadLine
- Write
- Read
- Clear
- BackgroundColor
- ForegroundColor
- etc.

## Example 3: String Methods

**Goal:** See what methods are available on string

```json
{
  "code": "var text = \"hello\";\ntext.",
  "line": 1,
  "column": 5
}
```

**Expected completions:**
- Length
- ToUpper
- ToLower
- Substring
- Replace
- Split
- Contains
- StartsWith
- EndsWith
- Trim
- etc.

## Example 4: LINQ Methods

**Goal:** See LINQ extension methods

```json
{
  "code": "using System.Linq;\nvar numbers = new[] { 1, 2, 3 };\nnumbers.",
  "line": 2,
  "column": 8
}
```

**Expected completions:**
- Select
- Where
- OrderBy
- GroupBy
- First
- FirstOrDefault
- Any
- All
- Count
- Sum
- Average
- etc.

## Example 5: Type Completion

**Goal:** Complete type names

```json
{
  "code": "using System;\nDateTime.",
  "line": 1,
  "column": 9
}
```

**Expected completions:**
- Now
- UtcNow
- Today
- Parse
- TryParse
- DaysInMonth
- IsLeapYear
- etc.

## Example 6: Async/Await

**Goal:** See async methods on HttpClient

```json
{
  "code": "using System.Net.Http;\nvar client = new HttpClient();\nawait client.",
  "line": 2,
  "column": 13
}
```

**Expected completions:**
- GetAsync
- PostAsync
- PutAsync
- DeleteAsync
- SendAsync
- GetStringAsync
- GetByteArrayAsync
- etc.

## Example 7: Custom Assembly

**Goal:** Get completions from your own DLL

```json
{
  "code": "using MyApp.Services;\nvar service = new UserService();\nservice.",
  "line": 2,
  "column": 8,
  "references": ["/path/to/your/MyApp.dll"]
}
```

This will show methods and properties from your UserService class.

## Example 8: Property Access

**Goal:** Access properties on a type

```json
{
  "code": "var now = DateTime.Now;\nnow.",
  "line": 1,
  "column": 4
}
```

**Expected completions:**
- Year
- Month
- Day
- Hour
- Minute
- Second
- DayOfWeek
- Date
- TimeOfDay
- AddDays
- AddHours
- ToString
- etc.

## Example 9: Generic Type

**Goal:** See completions on Dictionary

```json
{
  "code": "using System.Collections.Generic;\nvar dict = new Dictionary<string, int>();\ndict.",
  "line": 2,
  "column": 5
}
```

**Expected completions:**
- Add
- Remove
- ContainsKey
- ContainsValue
- TryGetValue
- Keys
- Values
- Count
- Clear
- etc.

## Example 10: Static Class Members

**Goal:** See static members

```json
{
  "code": "using System;\nMath.",
  "line": 1,
  "column": 5
}
```

**Expected completions:**
- Abs
- Max
- Min
- Sqrt
- Pow
- Round
- Floor
- Ceiling
- PI
- E
- etc.

## Tips for Best Results

1. **Include using statements** - Makes sure types are available
   ```csharp
   using System;
   using System.Linq;
   using System.Collections.Generic;
   ```

2. **Use complete variable declarations** - Helps type inference
   ```csharp
   var list = new List<string>();  // Good
   list.                           // Now get completions
   ```

3. **Position cursor after dot** - For member access
   ```csharp
   myObject.█  // Put cursor here (after dot)
   ```

4. **Add custom references** - For your own assemblies
   ```json
   "references": ["/full/path/to/your/assembly.dll"]
   ```

5. **Increase maxResults** - If you need more suggestions
   ```json
   "maxResults": 100
   ```

## Common Patterns

### Pattern 1: Method Chaining
```json
{
  "code": "var result = \"hello\".ToUpper().",
  "line": 0,
  "column": 32
}
```

### Pattern 2: After 'new'
```json
{
  "code": "var obj = new ",
  "line": 0,
  "column": 14
}
```
Shows available types.

### Pattern 3: After 'using'
```json
{
  "code": "using System.",
  "line": 0,
  "column": 13
}
```
Shows available namespaces.

### Pattern 4: Lambda Parameter
```json
{
  "code": "var numbers = new[] { 1, 2, 3 };\nnumbers.Select(x => x.",
  "line": 1,
  "column": 22
}
```
Shows methods on int (the type of x).

## Testing the Tool

You can test if the tool is working by trying the simplest example:

```json
{
  "code": "System.",
  "line": 0,
  "column": 7
}
```

This should return completions like: Console, String, DateTime, Math, Array, etc.

## Troubleshooting

**No completions returned:**
- Check line/column numbers are within bounds
- Ensure code has valid syntax up to cursor position
- Try adding using statements

**Type not found:**
- Add the DLL to `references` parameter
- Check the DLL path is absolute and exists

**Wrong completions:**
- Verify cursor position (line and column)
- Make sure type information is available (proper variable declaration)

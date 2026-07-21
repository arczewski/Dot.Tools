using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Detect shell environment at startup
var shellInfo = ShellEnvironment.Detect();
TerminalTools.CurrentShell = shellInfo;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

public sealed record ShellInfo(
    string DisplayName,
    string Executable,
    string ArgumentFlag,
    string Platform);

public static class ShellEnvironment
{
    public static ShellInfo Detect()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new ShellInfo("macOS zsh", "zsh", "-c", "macOS");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new ShellInfo("Linux bash", "bash", "-c", "Linux");

        var pwsh = FindOnPath("pwsh.exe") ?? FindOnPath("pwsh");
        return pwsh != null
            ? new ShellInfo("Windows PowerShell Core (pwsh)", "pwsh.exe", "-Command", "Windows")
            : new ShellInfo("Windows PowerShell", "powershell.exe", "-Command", "Windows");
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}

[McpServerToolType]
public static class TerminalTools
{
    internal static ShellInfo CurrentShell { get; set; } = ShellEnvironment.Detect();

    [McpServerTool]
    [Description(
        "Execute a shell command. The actual shell (zsh / bash / PowerShell) is determined at server startup based on the host OS. " +
        "Use maxOutputLines to limit the number of lines returned (default 500, -1 = unlimited). Output beyond this limit is truncated " +
        "(first lines kept). For potentially large outputs, consider: (1) use OS tools like head/tail/Select-Object, (2) redirect output " +
        "to a file and then read parts of that file with the file tools, or (3) increase maxOutputLines cautiously.")]
    public static async Task<string> RunCommand(
        string command,
        string? workingDirectory = null,
        int timeoutSeconds = 30,
        int maxOutputLines = 500)
    {
        if (workingDirectory != null && !Directory.Exists(workingDirectory))
            return $"Error: Working directory not found: {workingDirectory}";

        var shell = CurrentShell;

        var psi = new ProcessStartInfo
        {
            FileName = shell.Executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (shell.Platform == "Windows")
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add(shell.ArgumentFlag);
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add(shell.ArgumentFlag);
            psi.ArgumentList.Add(command);
        }

        if (workingDirectory != null)
            psi.WorkingDirectory = workingDirectory;

        try
        {
            using var process = new Process { StartInfo = psi };
            var stdoutBuf = new StringBuilder();
            var stderrBuf = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) stdoutBuf.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderrBuf.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (timeoutSeconds > 0)
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                await process.WaitForExitAsync(cts.Token);

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    var timedOutOutput = BuildOutput(stdoutBuf, stderrBuf, process.ExitCode, maxOutputLines);
                    return $"Error: Command timed out after {timeoutSeconds} second(s).\n" +
                           $"Stdout so far:\n{timedOutOutput}";
                }
            }
            else
            {
                await process.WaitForExitAsync();
            }

            return BuildOutput(stdoutBuf, stderrBuf, process.ExitCode, maxOutputLines);
        }
        catch (OperationCanceledException)
        {
            return $"Error: Command timed out after {timeoutSeconds} second(s).";
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description(
        "Run multiple shell commands sequentially and return each result labelled. Stops on first failure unless continueOnError is true. " +
        "maxOutputLines applies per command (default 500, -1 = unlimited). See RunCommand for output truncation advice.")]
    public static async Task<string> RunCommands(
        string[] commands,
        string? workingDirectory = null,
        int timeoutSecondsEach = 30,
        bool continueOnError = false,
        int maxOutputLines = 500)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < commands.Length; i++)
        {
            var cmd = commands[i];
            sb.AppendLine($"── Command {i + 1}: {cmd}");
            var result = await RunCommand(cmd, workingDirectory, timeoutSecondsEach, maxOutputLines);
            sb.AppendLine(result);
            sb.AppendLine();

            if (!continueOnError && result.StartsWith("Error:"))
                break;
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool]
    [Description("Return information about the shell environment: platform, shell name, executable, and argument flag.")]
    public static string GetShellInfo()
    {
        var s = CurrentShell;
        return $"Platform  : {s.Platform}\n" +
               $"Shell     : {s.DisplayName}\n" +
               $"Executable: {s.Executable}\n" +
               $"Flag      : {s.ArgumentFlag}";
    }

    private static string BuildOutput(StringBuilder stdout, StringBuilder stderr, int exitCode, int maxLines)
    {
        var stdoutLines = stdout.ToString().Split(Environment.NewLine).ToList();
        var stderrLines = stderr.ToString().Split(Environment.NewLine).ToList();

        // Apply maxOutputLines limit to combined output lines
        int total = stdoutLines.Count + stderrLines.Count;
        bool truncated = false;
        if (maxLines >= 0 && total > maxLines)
        {
            // Keep the first maxLines lines overall, distributing across stdout/stderr
            truncated = true;
            if (stdoutLines.Count >= maxLines)
            {
                stdoutLines = stdoutLines.Take(maxLines).ToList();
                stderrLines.Clear();
            }
            else
            {
                // All stdout can fit, fill remainder with stderr
                int remaining = maxLines - stdoutLines.Count;
                stderrLines = stderrLines.Take(remaining).ToList();
            }
        }

        var sb = new StringBuilder();
        if (stdoutLines.Count > 0)
            sb.Append(string.Join(Environment.NewLine, stdoutLines));
        if (stderrLines.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("[stderr]").AppendLine().Append(string.Join(Environment.NewLine, stderrLines));
        }
        if (truncated)
            sb.AppendLine().Append($"[Output truncated to {maxLines} lines. Use OS tools or redirect to file for full output.]");
        if (exitCode != 0)
            sb.AppendLine().Append($"[Exit code: {exitCode}]");

        return sb.Length > 0 ? sb.ToString().TrimEnd() : "(no output)";
    }
}
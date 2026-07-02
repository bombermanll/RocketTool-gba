using Avalonia;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RocketTool.Avalonia;

class Program
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Environment.ExitCode = 1;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static void ReportStartupFailure(Exception exception)
    {
        var logPath = TryWriteStartupLog(exception);
        var root = exception.GetBaseException();
        var message = new StringBuilder()
            .AppendLine("火箭队修改工具启动失败。")
            .AppendLine()
            .AppendLine(Truncate(root.Message, 1200));

        if (logPath is not null)
        {
            message.AppendLine()
                .AppendLine("完整错误日志已保存到：")
                .AppendLine(logPath)
                .AppendLine()
                .Append("请将该日志文件发送给开发者。");
        }
        else
        {
            message.AppendLine()
                .Append("错误日志写入失败，请截图并发送此错误信息。");
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                MessageBoxW(IntPtr.Zero, message.ToString(), "火箭队修改工具 - 启动失败", MbOk | MbIconError | MbSetForeground);
                return;
            }
            catch
            {
                // Fall through to stderr if the native dialog itself is unavailable.
            }
        }

        Console.Error.WriteLine(message);
        Console.Error.WriteLine(exception);
    }

    private static string? TryWriteStartupLog(Exception exception)
    {
        var fileName = $"startup-error-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RocketTool", "logs"),
            Path.Combine(Path.GetTempPath(), "RocketTool", "logs")
        };

        foreach (var directory in candidates)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, fileName);
                File.WriteAllText(path, BuildStartupLog(exception), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                return path;
            }
            catch
            {
                // Try the next writable location.
            }
        }

        return null;
    }

    private static string BuildStartupLog(Exception exception)
    {
        var output = new StringBuilder();
        output.AppendLine($"Time: {DateTimeOffset.Now:O}");
        output.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        output.AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
        output.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        output.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        output.AppendLine($"App version: {typeof(Program).Assembly.GetName().Version}");
        output.AppendLine($"Executable: {Environment.ProcessPath ?? "unknown"}");
        output.AppendLine($"Command line: {Environment.CommandLine}");
        output.AppendLine();
        output.AppendLine(exception.ToString());
        return output.ToString();
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

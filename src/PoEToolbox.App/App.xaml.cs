using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using LibBundle3;
using PoEToolbox.Shared;
using LibDat2;

namespace PoEToolbox.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        FileLogger.App.Info("App starting.");
        DispatcherUnhandledException += (_, args) =>
        {
            // 必须用 ToString() 而不是 $"{Type}: {Message}" + StackTrace：
            // XamlParseException 的 Message 只是一句「在某扩展上提供临时值时引发异常」，
            // 真正的行号、缺失的资源键全在 InnerException 里，拆开打印就等于什么都没说。
            // 另外调试期在弹窗里显示完整链，主人截屏就能直接看到根因，不必去翻日志。
            var detailed = args.Exception.ToString();
            var msg = $"Unhandled: {detailed}";
            FileLogger.WriteCritical(msg, args.Exception);
            MessageBox.Show(msg, "Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 界面上有 31 处 async void：没有这两道兜底，fire-and-forget 任务里的异常会静默消失，
        // 用户只看到"点了没反应"，日志里也什么都查不到。
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLogger.WriteCritical("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            FileLogger.WriteCritical("Unhandled exception on a non-UI thread.",
                args.ExceptionObject as Exception);
        };

        ConfigureOodleNativeLibrary();

        try
        {
            // 定义文件内嵌在 LibDat2 里，随程序走，不读 exe 旁的文件
            DatContainer.ReloadDefinitionsFromEmbedded();
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical("Failed to load embedded DAT definitions.", ex);
        }

        var main = new MainWindow();
        MainWindow = main;

        // Show() 失败必须显式退出，绝不能让进程留下来空转。
        // 症状回顾（2026-10-07）：窗口创建失败时 Application.Run 会进到
        // PushFrameImpl → GetMessageW 死等一条永远不会来的消息，CPU 增量为 0、HWND=0、
        // OnExit 永不执行，ShutdownMode=OnMainWindowClose 也就永远不触发 —— 表现为
        // 「窗口关掉了但进程退不掉」，而且日志里只剩一行 App starting.，毫无线索。
        try
        {
            main.Show();
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical("MainWindow.Show() failed; shutting down instead of idling with no window.", ex);
            Shutdown(1);
            return;
        }

        // Show() 不抛异常也可能没建出原生窗口（例如资源解析失败被上层吞掉）。
        // 用 IsVisible 兜一次底：这里拿不到可见窗口就说明启动已经废了，立刻退出。
        if (!main.IsVisible)
        {
            FileLogger.WriteCritical(
                "MainWindow.Show() returned without a visible window; shutting down instead of idling with no window.");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLogger.App.Info($"App exiting (code {e.ApplicationExitCode}).");
        base.OnExit(e);
    }

    private static void ConfigureOodleNativeLibrary()
    {
        var dllPath = ExtractEmbeddedDll("oo2core.dll");
        NativeLibrary.SetDllImportResolver(typeof(Oodle).Assembly, (libraryName, _, _) =>
            libraryName.Equals("oo2core", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(dllPath)
                : IntPtr.Zero);
    }

    private static string ExtractEmbeddedDll(string filename)
    {
        var directory = Path.Combine(ConfigService.DataDirectory, "native");
        Directory.CreateDirectory(directory);
        var dest = Path.Combine(directory, filename);

        try
        {
            var asm = typeof(App).Assembly;
            var resourceName = $"PoEToolbox.App.{filename}";
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) throw new FileNotFoundException($"Embedded resource was not found: {resourceName}");

            // 已存在且大小一致就复用；大小不同（程序升级换了原生库）就重写，避免永远用着旧的那份
            if (File.Exists(dest) && new FileInfo(dest).Length == stream.Length)
                return dest;

            using var fs = File.Create(dest);
            stream.CopyTo(fs);
            return dest;
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical($"Failed to extract embedded native library: {filename}", ex);
            throw;
        }
    }
}

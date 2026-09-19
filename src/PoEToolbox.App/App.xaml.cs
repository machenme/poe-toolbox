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
            var msg = $"Unhandled: {args.Exception.GetType().Name}: {args.Exception.Message}\n{args.Exception.StackTrace}";
            FileLogger.WriteCritical(msg, args.Exception);
            MessageBox.Show(msg, "Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
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

        ThemeManager.AppResources = Application.Current.Resources;
        ThemeManager.Apply(ThemeManager.Theme.Light);

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
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

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
            var asm = typeof(App).Assembly;
            using var stream = asm.GetManifestResourceStream("PoEToolbox.App.DatDefinitions.json");
            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                DatContainer.ReloadDefinitions(ms.ToArray());
            }
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
        if (File.Exists(dest)) return dest;

        try
        {
            var asm = typeof(App).Assembly;
            var resourceName = $"PoEToolbox.App.{filename}";
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) throw new FileNotFoundException($"Embedded resource was not found: {resourceName}");
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

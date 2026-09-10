using System.IO;
using System.Windows;
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

        ExtractEmbeddedDll("oo2core.dll");

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

    private static void ExtractEmbeddedDll(string filename)
    {
        var dest = Path.Combine(AppContext.BaseDirectory, filename);
        if (File.Exists(dest)) return;
        try
        {
            var asm = typeof(App).Assembly;
            var resourceName = $"PoEToolbox.App.{filename}";
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return;
            using var fs = File.Create(dest);
            stream.CopyTo(fs);
        }
        catch (Exception ex)
        {
            FileLogger.WriteCritical($"Failed to extract embedded native library: {filename}", ex);
        }
    }
}

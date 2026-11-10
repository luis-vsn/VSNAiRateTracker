using Avalonia;
using System;

namespace VSNAiRateTracker;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--encrypt-defaults", string inputPath, string outputPath])
        {
            try
            {
                VSNAiRateTracker.Services.BundledDefaultsCrypto.EncryptFile(inputPath, outputPath);
                Console.WriteLine($"Successfully encrypted defaults to: {outputPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to encrypt defaults: {ex.Message}");
                return 1;
            }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // macOS: Menu Bar-only app. Info.plist LSUIElement covers the .app bundle; this also
            // hides the Dock icon when running unbundled (dotnet run / raw publish output).
            .With(new MacOSPlatformOptions { ShowInDock = false })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CNC.Controls;
using CNC.Core;

namespace RenderHarness
{
    // One entry per renderable scenario. Add more as new dialogs need this kind of verification -
    // each just needs to build a real Window instance and return it; the Main() below handles
    // loading AppConfig, rendering, and saving the PNG.
    static class Scenarios
    {
        public static readonly Dictionary<string, Func<Window>> All = new Dictionary<string, Func<Window>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FixtureEditDialog"] = () =>
            {
                var fixture = new Fixture
                {
                    Name = "SmallFence",
                    Kind = FixtureKind.CornerFence,
                    Coords = "195.479,-599.379,-102.731"
                };
                return new FixtureEditDialog(fixture, null);
            },
            ["FixtureEditDialog.Vise"] = () =>
            {
                var fixture = new Fixture
                {
                    Name = "MainVise",
                    Kind = FixtureKind.MachinistVise,
                    Coords = "195.479,-599.379,-102.731"
                };
                return new FixtureEditDialog(fixture, null);
            },
            ["AppMessageBox"] = () =>
                new AppMessageBox("This is a test message to check whether DialogScaling.Apply is actually scaling this window.",
                    "Startup message", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.None),
        };
    }

    class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length < 1 || !Scenarios.All.TryGetValue(args[0], out var factory))
            {
                Console.WriteLine("Usage: render-harness.exe <scenario> [outputPath.png]");
                Console.WriteLine("Available scenarios:");
                foreach (var name in Scenarios.All.Keys)
                    Console.WriteLine("  " + name);
                return 1;
            }
            string outPath = args.Length > 1 ? args[1] : (args[0].Replace(".", "_") + ".png");

            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            // Real on-disk config (whatever profile "ioSender" resolves to on this machine) - read-only,
            // this harness never calls Save(). Must run before touching AppConfig.Settings.Base (it's
            // null until LoadConfig runs).
            AppConfig.Settings.LoadConfig("ioSender");
            if (AppConfig.Settings.Base == null)
            {
                Console.WriteLine("AppConfig.Settings.Base is still null after LoadConfig - can't render faithfully.");
                return 1;
            }

            Console.WriteLine("AppConfig.Settings.Base.UiScale = " + AppConfig.Settings.Base.UiScale);

            var window = factory();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Show();

            // LayoutTransform-driven regrowth (UiScale zoom) can take several dispatcher passes to
            // settle - pump until it does, not just once, or you get a plausible-looking but wrong
            // (smaller/clipped) capture.
            for (int i = 0; i < 10; i++)
            {
                window.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.Background, new System.Action(() => { }));
            }

            double w = window.ActualWidth, h = window.ActualHeight;
            Console.WriteLine("Rendered '" + args[0] + "' at " + w + " x " + h);

            var rtb = new RenderTargetBitmap((int)(w * 2), (int)(h * 2), 96 * 2, 96 * 2, PixelFormats.Pbgra32);
            rtb.Render(window);

            using (var fs = File.Create(outPath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                encoder.Save(fs);
            }
            Console.WriteLine("Saved " + Path.GetFullPath(outPath));

            window.Close();
            app.Shutdown();
            return 0;
        }
    }
}

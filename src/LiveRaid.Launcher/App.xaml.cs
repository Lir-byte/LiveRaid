using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LiveRaid.Launcher
{
    public partial class App : Application
    {
        /// <summary>
        /// Checks without clicking (for development): "--switch Single|Online|Stable|Test &lt;game folder&gt; &lt;log file&gt;" switches the mode or the branch and writes the log,
        /// "--snapshot &lt;png&gt; [game folder]" renders the window into a picture after a few seconds.
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            string[] a = e.Args;
            if (a.Length >= 4 && a[0] == "--switch")
            {
                Mode target = a[1] == "Online" ? Mode.Online : Mode.Single;
                bool branch = a[1] == "Stable" || a[1] == "Test";
                using (StreamWriter log = new StreamWriter(a[3], false))
                {
                    try
                    {
                        GameInstall game = new GameInstall(a[2]);
                        if (branch)
                        {
                            game.SwitchChannel(a[1] == "Test" ? Channel.Test : Channel.Stable, (text, level) => log.WriteLine(level + " " + text));
                        }
                        else
                        {
                            game.SwitchTo(target, (text, level) => log.WriteLine(level + " " + text));
                        }

                        log.WriteLine("OK");
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine("FAILED " + ex.Message);
                    }
                }

                Shutdown();
                return;
            }

            MainWindow w = new MainWindow();
            if (a.Length >= 2 && a[0] == "--snapshot")
            {
                if (a.Length >= 3)
                {
                    w.ForcedRoot = a[2];
                }

                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = -4000;
                w.Top = 0;
                w.ShowActivated = false;
                w.Show();
                DispatcherTimer t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(a.Contains("--long") ? 9 : 5) };
                t.Tick += (s, ev) =>
                {
                    t.Stop();
                    FrameworkElement root = (FrameworkElement)w.Content;
                    RenderTargetBitmap bmp = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    PngBitmapEncoder enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (FileStream f = File.Create(a[1]))
                    {
                        enc.Save(f);
                    }

                    Shutdown();
                };
                t.Start();
                return;
            }

            w.Show();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using WpfApp = System.Windows.Application;
using WpfWindow = System.Windows.Window;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMessageBoxButton = System.Windows.MessageBoxButton;
using WpfMessageBoxImage = System.Windows.MessageBoxImage;

using WpfBrushes = System.Windows.Media.Brushes;
using WpfGrid = System.Windows.Controls.Grid;
using WpfPanel = System.Windows.Controls.Panel;
using WpfImage = System.Windows.Controls.Image;

using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfVerticalAlignment = System.Windows.VerticalAlignment;

using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;


using Screen = System.Windows.Forms.Screen;

namespace ArtnetImageViewer;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var cfgPath = Path.Combine(baseDir, "config.ini");
            var cfg = Ini.Read(cfgPath);

            // --- Art-Net config ---
            var bindIp = cfg.Get("Artnet", "BindIP", "0.0.0.0");
            var port = cfg.GetInt("Artnet", "Port", 6454);
            var universeFilter = cfg.GetInt("Artnet", "Universe", 0); // -1 = any
            var senderIpFilter = cfg.Get("Artnet", "SenderIP", "").Trim();

            // --- Display ---
            var displayIndex = cfg.GetInt("Display", "Index", 0);

            // --- Images ---
            var images = ParseImages(cfg, baseDir);
            if (images.Count == 0)
                throw new Exception("Keine Einträge in [Images] gefunden.");

            var screens = Screen.AllScreens;
            if (screens.Length == 0)
                throw new Exception("Keine Displays gefunden.");

            if (displayIndex < 0 || displayIndex >= screens.Length)
                throw new Exception($"Ungültiger Display Index={displayIndex}. Verfügbar: 0..{screens.Length - 1}");

            var targetScreen = screens[displayIndex];
            var boundsPx = targetScreen.Bounds; // pixel coords

            // DPI: convert pixel bounds to WPF DIPs (device independent pixels)
            uint dpiX, dpiY;
            bool haveDpi = DpiHelper.TryGetMonitorDpi(boundsPx.Left + 1, boundsPx.Top + 1, out dpiX, out dpiY);
            if (!haveDpi || dpiX == 0 || dpiY == 0) { dpiX = dpiY = 96; }

            double scaleX = 96.0 / dpiX;
            double scaleY = 96.0 / dpiY;

            double leftDip = boundsPx.Left * scaleX;
            double topDip = boundsPx.Top * scaleY;
            double widthDip = boundsPx.Width * scaleX;
            double heightDip = boundsPx.Height * scaleY;

            // Shared DMX (512 bytes)
            var dmx = new byte[512];
            var dmxLock = new object();
            bool haveFrame = false;

            var cts = new CancellationTokenSource();

            // Listener
            var listener = new ArtNetListener(bindIp, port, senderIpFilter, universeFilter);
            listener.OnDmx += payload =>
            {
                lock (dmxLock)
                {
                    Array.Clear(dmx, 0, dmx.Length);
                    Array.Copy(payload, 0, dmx, 0, Math.Min(payload.Length, 512));
                    haveFrame = true;
                }
            };

            try
            {
                listener.Start(cts.Token);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(
                    $"Art-Net Listener konnte nicht gestartet werden:\n{ex.Message}\n\nDas Programm läuft weiter (Anzeige bleibt schwarz, bis Art-Net empfangen wird).",
                    "ArtnetImageViewer",
                    WpfMessageBoxButton.OK,
                    WpfMessageBoxImage.Warning
                );
            }

            // --- WPF Window ---
            var app = new WpfApp();

            var win = new WpfWindow
            {
                WindowStyle = System.Windows.WindowStyle.None,
                ResizeMode = System.Windows.ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = true,
                Background = WpfBrushes.Black,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = leftDip,
                Top = topDip,
                Width = widthDip,
                Height = heightDip
            };

            var grid = new WpfGrid { Background = WpfBrushes.Black };
            win.Content = grid;

            // Stack image layers
            var layers = new List<ImageLayer>();
            int z = 0;

            foreach (var entry in images.OrderBy(i => i.Channel))
            {
                var bitmap = LoadBitmap(entry.FullPath);

                var imgControl = new WpfImage
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform, // aspect-fit
                    HorizontalAlignment = WpfHorizontalAlignment.Center,
                    VerticalAlignment = WpfVerticalAlignment.Center,
                    Opacity = 0.0
                };

                WpfPanel.SetZIndex(imgControl, z++);
                grid.Children.Add(imgControl);

                layers.Add(new ImageLayer(entry.Name, entry.Channel, imgControl));
            }

            // 30 FPS update
            var timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };

            timer.Tick += (_, __) =>
            {
                if (!haveFrame) return;

                byte[] local = new byte[512];
                lock (dmxLock)
                {
                    Array.Copy(dmx, local, 512);
                }

                foreach (var layer in layers)
                {
                    byte v = local[layer.Channel - 1];
                    layer.Control.Opacity = v / 255.0;
                }
            };

            win.Closed += (_, __) =>
            {
                try { timer.Stop(); } catch { }
                try { cts.Cancel(); } catch { }
                try { listener.Dispose(); } catch { }
            };

            timer.Start();
            app.Run(win);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(ex.Message, "ArtnetImageViewer", WpfMessageBoxButton.OK, WpfMessageBoxImage.Error);
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.CacheOption = BitmapCacheOption.OnLoad; // don't lock file
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static List<ImageEntry> ParseImages(Ini cfg, string baseDir)
    {
        var section = cfg.GetSection("Images");
        var result = new List<ImageEntry>();

        foreach (var (name, raw) in section)
        {
            var v = raw.Trim();
            if (v.Length == 0) continue;

            string[] parts;
            if (v.Contains('|')) parts = v.Split('|', 2);
            else if (v.Contains(',')) parts = v.Split(',', 2);
            else throw new Exception($"Ungültiger Images-Eintrag '{name}'. Erwartet: Pfad | Kanal  (z.B. .\\bilder\\logo.jpg | 1)");

            var pathRaw = parts[0].Trim().Trim('"');
            var chRaw = parts[1].Trim();

            if (!int.TryParse(chRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ch))
                throw new Exception($"Ungültiger DMX-Kanal bei '{name}': {chRaw}");

            if (ch < 1 || ch > 512)
                throw new Exception($"DMX-Kanal außerhalb 1..512 bei '{name}': {ch}");

            var fullPath = Path.IsPathRooted(pathRaw)
                ? pathRaw
                : Path.GetFullPath(Path.Combine(baseDir, pathRaw));

            if (!File.Exists(fullPath))
                throw new Exception($"Bilddatei nicht gefunden für '{name}': {fullPath}");

            result.Add(new ImageEntry(name, fullPath, ch));
        }

        return result;
    }

    private readonly record struct ImageEntry(string Name, string FullPath, int Channel);
    private sealed record ImageLayer(string Name, int Channel, WpfImage Control);
}

internal sealed class ArtNetListener : IDisposable
{
    private readonly string _bindIp;
    private readonly int _port;
    private readonly string _senderIpFilter;
    private readonly int _universeFilter;

    private UdpClient? _udp;
    private Task? _loopTask;

    public event Action<byte[]>? OnDmx;

    public ArtNetListener(string bindIp, int port, string senderIpFilter, int universeFilter)
    {
        _bindIp = bindIp;
        _port = port;
        _senderIpFilter = senderIpFilter;
        _universeFilter = universeFilter;
    }

    public void Start(CancellationToken token)
    {
        if (_udp != null) return;

        var bindAddress = IPAddress.Parse(_bindIp);
        var localEp = new IPEndPoint(bindAddress, _port);

        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(localEp);

        // Ensure ReceiveAsync unblocks on cancellation
        token.Register(() =>
        {
            try { _udp?.Close(); } catch { }
        });

        _loopTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udp.ReceiveAsync().ConfigureAwait(false);
                }
                catch
                {
                    break; // cancelled / socket closed
                }

                if (!string.IsNullOrWhiteSpace(_senderIpFilter) &&
                    result.RemoteEndPoint.Address.ToString() != _senderIpFilter)
                    continue;

                if (!TryParseArtDmx(result.Buffer, _universeFilter, out var payload))
                    continue;

                OnDmx?.Invoke(payload);
            }
        }, token);
    }

    private static bool TryParseArtDmx(byte[] bytes, int universeFilter, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (bytes.Length < 18) return false;

        // "Art-Net\0"
        if (!(bytes[0] == (byte)'A' && bytes[1] == (byte)'r' && bytes[2] == (byte)'t' && bytes[3] == (byte)'-' &&
              bytes[4] == (byte)'N' && bytes[5] == (byte)'e' && bytes[6] == (byte)'t' && bytes[7] == 0x00))
            return false;

        // OpCode little-endian: ArtDMX = 0x5000 -> 00 50
        if (!(bytes[8] == 0x00 && bytes[9] == 0x50))
            return false;

        // Universe little-endian at 14,15
        int universe = bytes[14] + (bytes[15] << 8);
        if (universeFilter != -1 && universe != universeFilter)
            return false;

        // Length big-endian at 16,17
        int len = (bytes[16] << 8) + bytes[17];
        if (len <= 0) return false;

        const int payloadStart = 18;
        if (bytes.Length < payloadStart + len) return false;

        payload = new byte[len];
        Array.Copy(bytes, payloadStart, payload, 0, len);
        return true;
    }

    public void Dispose()
    {
        try { _udp?.Close(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
    }
}

internal sealed class Ini
{
    private readonly Dictionary<string, Dictionary<string, string>> _data =
        new(StringComparer.OrdinalIgnoreCase);

    public static Ini Read(string path)
    {
        if (!File.Exists(path))
            throw new Exception($"Config nicht gefunden: {path}");

        var ini = new Ini();
        string section = "";

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith(";") || line.StartsWith("#")) continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line[1..^1].Trim();
                if (!ini._data.ContainsKey(section))
                    ini._data[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0) continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();

            if (!ini._data.ContainsKey(section))
                ini._data[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            ini._data[section][key] = val;
        }

        return ini;
    }

    public string Get(string section, string key, string fallback)
    {
        if (_data.TryGetValue(section, out var s) && s.TryGetValue(key, out var v))
            return v;
        return fallback;
    }

    public int GetInt(string section, string key, int fallback)
    {
        var v = Get(section, key, "");
        return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
    }

    public Dictionary<string, string> GetSection(string section)
    {
        return _data.TryGetValue(section, out var s)
            ? s
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

internal static class DpiHelper
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    // On older Windows Shcore.dll might be missing; we handle that in try/catch
    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public static bool TryGetMonitorDpi(int xPx, int yPx, out uint dpiX, out uint dpiY)
    {
        dpiX = dpiY = 96;
        try
        {
            var hMon = MonitorFromPoint(new POINT(xPx, yPx), MONITOR_DEFAULTTONEAREST);
            if (hMon == IntPtr.Zero) return false;

            int hr = GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            return hr == 0 && dpiX > 0 && dpiY > 0;
        }
        catch
        {
            return false;
        }
    }
}

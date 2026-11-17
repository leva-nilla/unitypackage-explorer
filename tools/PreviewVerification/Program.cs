using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Windows.Forms;

namespace UnityPackageExplorer.PreviewVerification;

/// <summary>
/// Activates the registered Explorer preview handler through the same surrogate
/// (prevhost.exe) that Explorer uses, then captures its child window to a PNG and
/// reports pixel statistics so a real-machine preview check can be machine-judged.
/// </summary>
internal static class Program
{
    private const string HandlerClsidText = "70C67CC6-949B-4B5D-A8F2-7844D5C29E76";
    private static readonly Guid HandlerClsid = new(HandlerClsidText);
    private const uint ClsctxLocalServer = 0x4;
    private const uint StgmRead = 0;
    private const uint FileAttributeNormal = 0x80;
    private const uint PwRenderFullContent = 0x00000002;
    private const int PollIntervalMilliseconds = 300;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        if (!Options.TryParse(args, out var options, out var parseError) || options is null)
        {
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        var result = new VerificationResult
        {
            File = Path.GetFullPath(options.FilePath),
            Stream = options.Stream,
            UnloadLimitMilliseconds = options.MaxUnloadMilliseconds
        };

        Application.EnableVisualStyles();
        using var host = new Form
        {
            Text = "UnityPackage Explorer preview verification",
            FormBorderStyle = FormBorderStyle.SizableToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(40, 40),
            ClientSize = new Size(options.Width, options.Height),
            ShowInTaskbar = false,
            BackColor = Color.FromArgb(22, 27, 34)
        };

        IPreviewHandler? preview = null;
        IStream? sourceStream = null;
        var unloaded = false;
        try
        {
            host.Show();
            host.Refresh();
            Pump(200);

            var hr = CoCreateInstance(in HandlerClsid, IntPtr.Zero, ClsctxLocalServer, in IidPreviewHandler, out var previewPointer);
            if (hr < 0)
            {
                return Fail(result, options, 3, $"CoCreateInstance(CLSCTX_LOCAL_SERVER) failed: 0x{hr:X8}. The handler is not registered for this machine or the .NET 10 Desktop Runtime is missing.");
            }

            var previewObject = Marshal.GetObjectForIUnknown(previewPointer);
            Marshal.Release(previewPointer);
            preview = (IPreviewHandler)previewObject;

            if (options.Stream)
            {
                var streamHr = SHCreateStreamOnFileEx(result.File, StgmRead, FileAttributeNormal, false, IntPtr.Zero, out sourceStream);
                if (streamHr < 0) return Fail(result, options, 4, $"SHCreateStreamOnFileEx failed: 0x{streamHr:X8}.");
                ((IInitializeWithStream)preview).Initialize(sourceStream, StgmRead);
            }
            else
            {
                ((IInitializeWithFile)preview).Initialize(result.File, StgmRead);
            }

            var bounds = new Rect { Left = 0, Top = 0, Right = options.Width, Bottom = options.Height };
            hr = preview.SetWindow(host.Handle, ref bounds);
            if (hr < 0) return Fail(result, options, 4, $"IPreviewHandler.SetWindow failed: 0x{hr:X8}.");
            hr = preview.DoPreview();
            if (hr < 0) return Fail(result, options, 4, $"IPreviewHandler.DoPreview failed: 0x{hr:X8}.");

            var capture = WaitForCapture(preview, host, options, result);
            if (capture is null)
            {
                return Fail(result, options, 6, "The preview handler did not create a capturable child window in time.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.CapturePath))!);
            capture.Image.Save(options.CapturePath, ImageFormat.Png);
            result.CapturePath = Path.GetFullPath(options.CapturePath);
            result.CaptureMethod = capture.Method;
            result.ChildWidth = capture.Width;
            result.ChildHeight = capture.Height;
            result.NonUniformRatio = Math.Round(capture.Stats.NonUniformRatio, 4);
            result.BlackRatio = Math.Round(capture.Stats.BlackRatio, 4);
            result.DistinctColors = capture.Stats.DistinctColors;
            result.MeanLuminance = Math.Round(capture.Stats.MeanLuminance, 1);
            result.ChromaticRatio = Math.Round(capture.Stats.ChromaticRatio, 4);
            result.CenterNonUniformRatio = Math.Round(capture.Stats.CenterNonUniformRatio, 4);

            if (capture.Stats.NonUniformRatio < options.MinimumNonUniformRatio)
            {
                return Fail(result, options, 7, $"Captured window looks blank: non-uniform ratio {capture.Stats.NonUniformRatio:P2} is below the {options.MinimumNonUniformRatio:P2} threshold.");
            }
            if (capture.Stats.ChromaticRatio < options.MinimumChromaticRatio)
            {
                return Fail(result, options, 7, $"Captured window shows no colored preview content: chromatic ratio {capture.Stats.ChromaticRatio:P2} is below the {options.MinimumChromaticRatio:P2} threshold. The preview may still be loading or only showing a status message.");
            }
            if (capture.Stats.CenterNonUniformRatio < options.MinimumCenterNonUniformRatio)
            {
                return Fail(result, options, 7, $"The center of the preview looks empty: center non-uniform ratio {capture.Stats.CenterNonUniformRatio:P2} is below the {options.MinimumCenterNonUniformRatio:P2} threshold. Nothing may have rendered in the preview viewport.");
            }

            var timer = Stopwatch.StartNew();
            hr = preview.Unload();
            timer.Stop();
            unloaded = true;
            result.UnloadMilliseconds = timer.ElapsedMilliseconds;
            if (hr < 0) return Fail(result, options, 8, $"IPreviewHandler.Unload failed: 0x{hr:X8}.");

            result.ChildWindowClosed = WaitForWindowClose(capture.ChildWindow, TimeSpan.FromSeconds(5));
            if (!result.ChildWindowClosed)
            {
                return Fail(result, options, 9, "The preview child window was still alive five seconds after Unload.");
            }
            if (timer.ElapsedMilliseconds > options.MaxUnloadMilliseconds)
            {
                return Fail(result, options, 8, $"Unload took {timer.ElapsedMilliseconds} ms, above the {options.MaxUnloadMilliseconds} ms limit.");
            }

            result.Succeeded = true;
            result.ExitCode = 0;
            Console.WriteLine($"PASS {Path.GetFileName(result.File)} stream={options.Stream} capture={result.CaptureMethod} nonUniform={result.NonUniformRatio:P2} center={result.CenterNonUniformRatio:P2} chromatic={result.ChromaticRatio:P2} colors={result.DistinctColors} unload={result.UnloadMilliseconds}ms");
            return 0;
        }
        catch (Exception exception)
        {
            return Fail(result, options, 5, $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (!unloaded)
            {
                try { preview?.Unload(); } catch (Exception) { }
            }
            if (sourceStream is not null) Marshal.ReleaseComObject(sourceStream);
            if (preview is not null) Marshal.ReleaseComObject(preview);
            WriteResult(result, options);
        }
    }

    private static int Fail(VerificationResult result, Options options, int exitCode, string message)
    {
        result.Succeeded = false;
        result.ExitCode = exitCode;
        result.Error = message;
        Console.Error.WriteLine($"FAIL {Path.GetFileName(result.File)} stream={options.Stream}: {message}");
        return exitCode;
    }

    private static void WriteResult(VerificationResult result, Options options)
    {
        if (string.IsNullOrWhiteSpace(options.JsonPath)) return;
        try
        {
            var path = Path.GetFullPath(options.JsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write the JSON result: {exception.Message}");
        }
    }

    private static CaptureAttempt? WaitForCapture(IPreviewHandler preview, Form host, Options options, VerificationResult result)
    {
        var deadline = Stopwatch.StartNew();
        var stableSince = Stopwatch.StartNew();
        CaptureAttempt? best = null;
        while (deadline.ElapsedMilliseconds < options.SettleMilliseconds)
        {
            Pump(PollIntervalMilliseconds);
            var child = TryGetChildWindow(preview);
            if (child != IntPtr.Zero)
            {
                result.ChildWindow = $"0x{child.ToInt64():X}";
                result.ChildVisible = IsWindowVisible(child);
                var candidate = CaptureWindow(child, "PrintWindow");
                if (candidate is not null && Beats(candidate.Stats, best, options))
                {
                    best?.Dispose();
                    best = candidate;
                    stableSince.Restart();
                }
                else
                {
                    candidate?.Dispose();
                }

                if (best is not null && IsReady(best.Stats, options) && stableSince.ElapsedMilliseconds >= 1_500) break;
            }

            if (options.AllowScreenCapture && (best is null || !IsReady(best.Stats, options)))
            {
                var screen = CaptureScreen(host);
                if (screen is not null && Beats(screen.Stats, best, options))
                {
                    best?.Dispose();
                    best = screen;
                    stableSince.Restart();
                }
                else
                {
                    screen?.Dispose();
                }
            }
        }
        return best;
    }

    /// <summary>Prefers a capture that already shows content, then the busier one.</summary>
    private static bool Beats(CaptureStats candidate, CaptureAttempt? best, Options options)
    {
        if (best is null) return true;
        var candidateReady = IsReady(candidate, options);
        var bestReady = IsReady(best.Stats, options);
        if (candidateReady != bestReady) return candidateReady;
        return candidate.NonUniformRatio > best.Stats.NonUniformRatio;
    }

    /// <summary>Waits for actual preview content: textured/colored rendering, not just status text.</summary>
    private static bool IsReady(CaptureStats stats, Options options) =>
        stats.NonUniformRatio >= options.MinimumNonUniformRatio
        && stats.DistinctColors >= 8
        && stats.ChromaticRatio >= options.MinimumChromaticRatio
        && stats.CenterNonUniformRatio >= options.MinimumCenterNonUniformRatio;

    private static CaptureAttempt? CaptureWindow(IntPtr hwnd, string method)
    {
        if (!GetClientRect(hwnd, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 16 || height < 16) return null;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try { PrintWindow(hwnd, hdc, PwRenderFullContent); }
            finally { graphics.ReleaseHdc(hdc); }
        }

        var stats = Analyze(bitmap);
        if (stats.NonUniformRatio < 0.002)
        {
            using var graphics = Graphics.FromImage(bitmap);
            var hdc = graphics.GetHdc();
            try { PrintWindow(hwnd, hdc, 0); }
            finally { graphics.ReleaseHdc(hdc); }
            stats = Analyze(bitmap);
        }
        return new CaptureAttempt(bitmap, stats, method, width, height, hwnd);
    }

    private static CaptureAttempt? CaptureScreen(Form host)
    {
        try
        {
            host.TopMost = true;
            host.Refresh();
            Pump(150);
            var rectangle = host.RectangleToScreen(host.ClientRectangle);
            if (rectangle.Width < 16 || rectangle.Height < 16) return null;
            var bitmap = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rectangle.Location, Point.Empty, rectangle.Size);
            }
            host.TopMost = false;
            return new CaptureAttempt(bitmap, Analyze(bitmap), "CopyFromScreen", rectangle.Width, rectangle.Height, IntPtr.Zero);
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException or Win32Exception)
        {
            host.TopMost = false;
            return null;
        }
    }

    private static CaptureStats Analyze(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), pixels, y * stride, stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var step = Math.Max(1, Math.Min(width, height) / 200);
        var counts = new Dictionary<int, int>();
        var centerCounts = new Dictionary<int, int>();
        var total = 0;
        var centerTotal = 0;
        var black = 0;
        var chromatic = 0;
        double luminance = 0;
        var centerLeft = (int)(width * 0.15);
        var centerRight = (int)(width * 0.85);
        var centerTop = (int)(height * 0.15);
        var centerBottom = (int)(height * 0.55);
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var offset = (y * stride) + (x * 4);
                int b = pixels[offset];
                int g = pixels[offset + 1];
                int r = pixels[offset + 2];
                var key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
                total++;
                if (x >= centerLeft && x <= centerRight && y >= centerTop && y <= centerBottom)
                {
                    centerCounts[key] = centerCounts.TryGetValue(key, out var centerCount) ? centerCount + 1 : 1;
                    centerTotal++;
                }
                if (r + g + b < 24) black++;
                if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 24) chromatic++;
                luminance += (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            }
        }

        if (total == 0) return new CaptureStats(0, 0, 0, 0, 0, 0);
        var mode = counts.Values.Max();
        var centerMode = centerCounts.Count == 0 ? 0 : centerCounts.Values.Max();
        return new CaptureStats(
            1d - ((double)mode / total),
            (double)black / total,
            counts.Count,
            luminance / total,
            (double)chromatic / total,
            centerTotal == 0 ? 0 : 1d - ((double)centerMode / centerTotal));
    }

    private static IntPtr TryGetChildWindow(IPreviewHandler preview)
    {
        try
        {
            ((IOleWindow)preview).GetWindow(out var child);
            return child;
        }
        catch (InvalidCastException)
        {
            return IntPtr.Zero;
        }
    }

    private static bool WaitForWindowClose(IntPtr hwnd, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (!IsWindow(hwnd)) return true;
            Pump(100);
        }
        return !IsWindow(hwnd);
    }

    private static void Pump(int milliseconds)
    {
        var deadline = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            Thread.Sleep(25);
        }
        while (deadline.ElapsedMilliseconds < milliseconds);
    }

    private static readonly Guid IidPreviewHandler = typeof(IPreviewHandler).GUID;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateStreamOnFileEx(string pszFile, uint grfMode, uint dwAttributes, bool fCreate, IntPtr pstmTemplate, out IStream ppstm);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    private sealed record CaptureStats(double NonUniformRatio, double BlackRatio, int DistinctColors, double MeanLuminance, double ChromaticRatio, double CenterNonUniformRatio);

    private sealed class CaptureAttempt(Bitmap image, CaptureStats stats, string method, int width, int height, IntPtr childWindow) : IDisposable
    {
        public Bitmap Image { get; } = image;
        public CaptureStats Stats { get; } = stats;
        public string Method { get; } = method;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public IntPtr ChildWindow { get; } = childWindow;

        public void Dispose() => Image.Dispose();
    }

    private sealed class VerificationResult
    {
        public string File { get; set; } = string.Empty;
        public bool Stream { get; set; }
        public bool Succeeded { get; set; }
        public int ExitCode { get; set; }
        public string? Error { get; set; }
        public string? CapturePath { get; set; }
        public string? CaptureMethod { get; set; }
        public string? ChildWindow { get; set; }
        public bool ChildVisible { get; set; }
        public int ChildWidth { get; set; }
        public int ChildHeight { get; set; }
        public double NonUniformRatio { get; set; }
        public double BlackRatio { get; set; }
        public int DistinctColors { get; set; }
        public double MeanLuminance { get; set; }
        public double ChromaticRatio { get; set; }
        public double CenterNonUniformRatio { get; set; }
        public long UnloadMilliseconds { get; set; }
        public long UnloadLimitMilliseconds { get; set; }
        public bool ChildWindowClosed { get; set; }
    }

    private sealed class Options
    {
        public const string Usage = """
            Usage: PreviewVerification --file <path> [options]

              --file <path>            .unitypackage / .zip / .rar / .prefab to preview
              --stream                 initialize through IInitializeWithStream instead of IInitializeWithFile
              --capture <png>          where to save the captured window (default: <file>.png next to this exe)
              --json <path>            write the full result as JSON
              --settle-ms <n>          how long to wait for the preview to render (default 25000)
              --min-nonuniform <0-1>   fail when the capture is flatter than this ratio (default 0.005)
              --min-chromatic <0-1>    fail when the capture has fewer colored pixels than this ratio (default 0.02)
              --min-center-nonuniform <0-1>
                                       fail when the center of the preview looks empty (default 0)
              --max-unload-ms <n>      fail when Unload takes longer than this (default 5000)
              --width <n> --height <n> host window size (default 900x600)
              --no-screen-capture      never fall back to copying pixels from the screen
              --help                   show this text

            Exit codes: 0 pass, 2 usage, 3 activation, 4 initialize/preview, 5 exception,
            6 no child window, 7 blank capture, 8 unload failed or too slow, 9 window survived Unload.
            """;

        public string FilePath { get; private init; } = string.Empty;
        public bool Stream { get; private init; }
        public string CapturePath { get; private init; } = string.Empty;
        public string? JsonPath { get; private init; }
        public int SettleMilliseconds { get; private init; } = 25_000;
        public double MinimumNonUniformRatio { get; private init; } = 0.005;
        public double MinimumChromaticRatio { get; private init; } = 0.02;
        public double MinimumCenterNonUniformRatio { get; private init; }
        public int MaxUnloadMilliseconds { get; private init; } = 5_000;
        public int Width { get; private init; } = 900;
        public int Height { get; private init; } = 600;
        public bool AllowScreenCapture { get; private init; } = true;

        public static bool TryParse(string[] args, out Options? options, out string error)
        {
            options = null;
            error = string.Empty;
            string? file = null;
            var stream = false;
            string? capture = null;
            string? json = null;
            var settle = 25_000;
            var minimumNonUniform = 0.005;
            var minimumChromatic = 0.02;
            var minimumCenterNonUniform = 0.0;
            var maxUnload = 5_000;
            var width = 900;
            var height = 600;
            var allowScreenCapture = true;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                string NextValue()
                {
                    if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {argument}.");
                    return args[++index];
                }

                try
                {
                    switch (argument)
                    {
                        case "--file": file = NextValue(); break;
                        case "--stream": stream = true; break;
                        case "--capture": capture = NextValue(); break;
                        case "--json": json = NextValue(); break;
                        case "--settle-ms": settle = int.Parse(NextValue()); break;
                        case "--min-nonuniform": minimumNonUniform = double.Parse(NextValue()); break;
                        case "--min-chromatic": minimumChromatic = double.Parse(NextValue()); break;
                        case "--min-center-nonuniform": minimumCenterNonUniform = double.Parse(NextValue()); break;
                        case "--max-unload-ms": maxUnload = int.Parse(NextValue()); break;
                        case "--width": width = int.Parse(NextValue()); break;
                        case "--height": height = int.Parse(NextValue()); break;
                        case "--no-screen-capture": allowScreenCapture = false; break;
                        default:
                            error = $"Unknown argument: {argument}";
                            return false;
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
                {
                    error = exception.Message;
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(file))
            {
                error = "--file is required.";
                return false;
            }
            if (!File.Exists(file))
            {
                error = $"Preview source does not exist: {file}";
                return false;
            }
            if (width < 200 || height < 150)
            {
                error = "The host window must be at least 200x150.";
                return false;
            }

            options = new Options
            {
                FilePath = file,
                Stream = stream,
                CapturePath = capture ?? Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(file) + ".png"),
                JsonPath = json,
                SettleMilliseconds = settle,
                MinimumNonUniformRatio = minimumNonUniform,
                MinimumChromaticRatio = minimumChromatic,
                MinimumCenterNonUniformRatio = minimumCenterNonUniform,
                MaxUnloadMilliseconds = maxUnload,
                Width = width,
                Height = height,
                AllowScreenCapture = allowScreenCapture
            };
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [ComImport]
    [Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        [PreserveSig] int SetWindow(IntPtr hwnd, ref Rect rect);
        [PreserveSig] int SetRect(ref Rect rect);
        [PreserveSig] int DoPreview();
        [PreserveSig] int Unload();
        [PreserveSig] int SetFocus();
        [PreserveSig] int QueryFocus(out IntPtr hwnd);
        [PreserveSig] int TranslateAccelerator(IntPtr message);
    }

    [ComImport]
    [Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
    }

    [ComImport]
    [Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize([MarshalAs(UnmanagedType.Interface)] IStream stream, uint mode);
    }

    [ComImport]
    [Guid("00000114-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }
}

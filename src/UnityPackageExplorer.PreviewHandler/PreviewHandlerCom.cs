using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.PreviewHandler;

[ComVisible(true)]
[Guid(ClassId)]
[ProgId("UnityPackageExplorer.PreviewHandler")]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PreviewHandlerCom : IPreviewHandler, IInitializeWithFile, IInitializeWithStream, IObjectWithSite
{
    public const string ClassId = "C24F7D7D-2C91-4AA9-969D-FADE6F9A17D2";
    private string? _path;
    private IntPtr _parent;
    private NativeRect _bounds;
    private Panel? _panel;
    private PictureBox? _picture;
    private Label? _summary;
    private object? _site;
    private System.Runtime.InteropServices.ComTypes.IStream? _stream;
    private PreviewContent? _content;

    public int Initialize(string filePath, uint mode)
    {
        _path = Path.GetFullPath(filePath);
        _stream = null;
        return 0;
    }

    public int Initialize(System.Runtime.InteropServices.ComTypes.IStream stream, uint mode)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _path = null;
        return 0;
    }

    public int SetSite(object? site)
    {
        _site = site;
        return 0;
    }

    public int GetSite(ref Guid interfaceId, out IntPtr site)
    {
        site = IntPtr.Zero;
        if (_site is null) return unchecked((int)0x80004002);
        var unknown = Marshal.GetIUnknownForObject(_site);
        try { return Marshal.QueryInterface(unknown, in interfaceId, out site); }
        finally { Marshal.Release(unknown); }
    }

    public int GetWindow(out IntPtr window)
    {
        window = _panel?.IsHandleCreated == true ? _panel.Handle : IntPtr.Zero;
        return window == IntPtr.Zero ? unchecked((int)0x80004005) : 0;
    }

    public int ContextSensitiveHelp(int enterMode) => unchecked((int)0x80004001);

    public int SetWindow(IntPtr parentWindow, ref NativeRect rect)
    {
        _parent = parentWindow;
        _bounds = rect;
        PositionPanel();
        return 0;
    }

    public int SetRect(ref NativeRect rect)
    {
        _bounds = rect;
        PositionPanel();
        return 0;
    }

    public int DoPreview()
    {
        if (string.IsNullOrWhiteSpace(_path) && _stream is null) return unchecked((int)0x80070002);
        try
        {
            _content = _stream is null ? PreviewContent.Read(_path!) : ReadStreamContent(_stream);
            _panel = new PreviewHostPanel { BackColor = Color.FromArgb(22, 27, 34), Padding = new Padding(14) };
            _picture = new PictureBox
            {
                Dock = DockStyle.Fill,
                Image = _content.Image,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(22, 27, 34)
            };
            _summary = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 34,
                Text = _content.Summary,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(213, 220, 229),
                BackColor = Color.FromArgb(32, 40, 51),
                Padding = new Padding(9, 0, 0, 0)
            };
            _panel.Controls.Add(_picture);
            _panel.Controls.Add(_summary);
            _panel.CreateControl();
            if (_parent != IntPtr.Zero)
            {
                NativeMethods.SetParent(_panel.Handle, _parent);
                PositionPanel();
            }
            _panel.Visible = true;
            return 0;
        }
        catch (Exception ex)
        {
            _panel?.Dispose();
            _panel = new PreviewHostPanel { BackColor = Color.FromArgb(22, 27, 34), Padding = new Padding(16) };
            _summary = new Label
            {
                Dock = DockStyle.Fill,
                Text = $"プレビューを作成できませんでした。\r\n{ex.Message}",
                ForeColor = Color.FromArgb(232, 237, 243),
                BackColor = Color.FromArgb(22, 27, 34),
                AutoEllipsis = true
            };
            _panel.Controls.Add(_summary);
            _panel.CreateControl();
            if (_parent != IntPtr.Zero)
            {
                NativeMethods.SetParent(_panel.Handle, _parent);
                PositionPanel();
            }
            _panel.Visible = true;
            return 0;
        }
    }

    public int Unload()
    {
        if (_panel is not null)
        {
            if (_picture is not null) _picture.Image = null;
            _panel.Dispose();
        }
        _panel = null;
        _picture = null;
        _summary = null;
        _content?.Dispose();
        _content = null;
        _path = null;
        _stream = null;
        _parent = IntPtr.Zero;
        _site = null;
        return 0;
    }

    public int SetFocus()
    {
        _panel?.Focus();
        return 0;
    }

    public int QueryFocus(out IntPtr window)
    {
        window = NativeMethods.GetFocus();
        return 0;
    }

    public int TranslateAccelerator(ref NativeMessage message) => 1; // S_FALSE: the host can handle the key.

    private void PositionPanel()
    {
        if (_panel?.IsHandleCreated != true || _parent == IntPtr.Zero) return;
        NativeMethods.SetParent(_panel.Handle, _parent);
        NativeMethods.SetWindowPos(
            _panel.Handle,
            IntPtr.Zero,
            _bounds.Left,
            _bounds.Top,
            Math.Max(0, _bounds.Right - _bounds.Left),
            Math.Max(0, _bounds.Bottom - _bounds.Top),
            0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
    }

    private static PreviewContent ReadStreamContent(System.Runtime.InteropServices.ComTypes.IStream stream)
    {
        string? name = null;
        try
        {
            stream.Stat(out var statistics, 0);
            name = statistics.pwcsName;
        }
        catch (Exception) { }

        var adapter = new ComStreamReader(stream);
        var extension = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
        if (extension is not (".unitypackage" or ".prefab"))
        {
            var header = new byte[512];
            var read = adapter.Read(header, 0, header.Length);
            adapter.Position = 0;
            if (read >= 2 && header[0] == 0x1f && header[1] == 0x8b) extension = ".unitypackage";
            else if (read >= 262 && Encoding.ASCII.GetString(header, 257, 5) == "ustar") extension = ".unitypackage";
            else
            {
                var text = Encoding.UTF8.GetString(header, 0, read).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                if (text.StartsWith("%YAML", StringComparison.Ordinal) || text.StartsWith("--- !u!", StringComparison.Ordinal)) extension = ".prefab";
            }
        }

        var title = string.IsNullOrWhiteSpace(name) ? $"Unity Asset{extension}" : Path.GetFileName(name);
        if (extension == ".unitypackage")
            return PreviewContent.ReadPackage(title, new UnityPackageReader(() =>
            {
                var source = new ComStreamReader(stream);
                source.Position = 0;
                return source;
            }));
        if (extension == ".prefab")
        {
            adapter.Position = 0;
            using var reader = new StreamReader(adapter, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return PreviewContent.ReadPrefab(title, reader.ReadToEnd());
        }

        return PreviewContent.Read(title);
    }
}

[ComVisible(true)]
[Guid("b7d14566-0509-4cce-a71f-0a554233bd9b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithFile
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.LPWStr)] string filePath, uint mode);
}

[ComVisible(true)]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IStream stream, uint mode);
}

[ComVisible(true)]
[Guid("fc4801a3-2ba9-11cf-a229-00aa003d7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSite
{
    [PreserveSig] int SetSite([MarshalAs(UnmanagedType.IUnknown)] object? site);
    [PreserveSig] int GetSite(ref Guid interfaceId, out IntPtr site);
}

[ComVisible(true)]
[Guid("00000114-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleWindow
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp(int enterMode);
}

[ComVisible(true)]
[Guid("8895b1c6-b41f-4c1c-a562-0d564250836f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandler : IOleWindow
{
    [PreserveSig] int SetWindow(IntPtr parentWindow, ref NativeRect rect);
    [PreserveSig] int SetRect(ref NativeRect rect);
    [PreserveSig] int DoPreview();
    [PreserveSig] int Unload();
    [PreserveSig] int SetFocus();
    [PreserveSig] int QueryFocus(out IntPtr window);
    [PreserveSig] int TranslateAccelerator(ref NativeMessage message);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeMessage
{
    public IntPtr Window;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int X;
    public int Y;
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();
}

internal sealed class ComStreamReader(System.Runtime.InteropServices.ComTypes.IStream source) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            source.Stat(out var statistics, 1);
            return statistics.cbSize;
        }
    }
    public override long Position
    {
        get => Seek(0, SeekOrigin.Current);
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        var target = offset == 0 ? buffer : new byte[count];
        var readPointer = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            Marshal.WriteInt32(readPointer, 0);
            source.Read(target, count, readPointer);
            var read = Math.Clamp(Marshal.ReadInt32(readPointer), 0, count);
            if (offset != 0 && read > 0) Array.Copy(target, 0, buffer, offset, read);
            return read;
        }
        finally { Marshal.FreeCoTaskMem(readPointer); }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var positionPointer = Marshal.AllocCoTaskMem(sizeof(long));
        try
        {
            source.Seek(offset, (int)origin, positionPointer);
            return Marshal.ReadInt64(positionPointer);
        }
        finally { Marshal.FreeCoTaskMem(positionPointer); }
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { }
}

internal sealed class PreviewHostPanel : Panel
{
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style |= 0x40000000; // WS_CHILD, required when hosted by Prevhost.
            return parameters;
        }
    }
}

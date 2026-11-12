using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Forms.Integration;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX;
using UnityPackageExplorer.Rendering;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.PreviewHandler;

/// <summary>The small, native child surface hosted inside Explorer's preview window.</summary>
internal sealed class ExplorerPreviewSurface : IDisposable
{
    private readonly ElementHost _host;
    private readonly System.Windows.Controls.ComboBox _prefabs;
    private readonly Viewport3DX _viewport;
    private readonly SceneNodeGroupModel3D _models;
    private readonly System.Windows.Controls.Image _image;
    private readonly TextBlock _text;
    private readonly TextBlock _summary;
    private readonly TextBlock _warnings;
    private readonly HelixToolkit.SharpDX.DefaultEffectsManager _effectsManager;
    private IReadOnlyList<UnityPackageEntry> _candidateEntries = [];
    private PreparedPreview? _prepared;
    private string? _preparedTempRoot;
    private bool _disposed;

    public ExplorerPreviewSurface()
    {
        _prefabs = new System.Windows.Controls.ComboBox { Margin = new Thickness(8), MinHeight = 28, IsEnabled = false };
        _prefabs.Items.Add("読み込み中…");
        _prefabs.SelectedIndex = 0;
        _prefabs.SelectionChanged += (_, _) =>
        {
            if (_prefabs.SelectedIndex >= 0 && _prefabs.SelectedIndex < _candidateEntries.Count)
                CandidateSelected?.Invoke(_candidateEntries[_prefabs.SelectedIndex]);
        };
        _effectsManager = new HelixToolkit.SharpDX.DefaultEffectsManager();
        _viewport = new Viewport3DX
        {
            BackgroundColor = System.Windows.Media.Color.FromRgb(24, 29, 36),
            EffectsManager = _effectsManager,
            Camera = new PerspectiveCamera { Position = new System.Windows.Media.Media3D.Point3D(0, 2, 6), LookDirection = new System.Windows.Media.Media3D.Vector3D(0, -1, -6), UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, 0), FieldOfView = 45 },
            ShowCoordinateSystem = true,
            IsRotationEnabled = true,
            IsPanEnabled = false,
            IsZoomEnabled = true,
            Visibility = Visibility.Collapsed
        };
        _models = new SceneNodeGroupModel3D();
        _viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.LeftClick)));
        _viewport.Items.Add(_models);
        _viewport.Items.Add(new AmbientLight3D { Color = Colors.White });
        _viewport.Items.Add(new DirectionalLight3D { Color = Colors.White, Direction = new System.Windows.Media.Media3D.Vector3D(-1, -2, -3) });
        _image = new System.Windows.Controls.Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed, Margin = new Thickness(8) };
        _text = new TextBlock { Foreground = System.Windows.Media.Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new Thickness(20), Text = "プレビューを準備しています…" };
        var center = new Grid { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 29, 36)) };
        center.Children.Add(_viewport);
        center.Children.Add(_image);
        center.Children.Add(_text);

        var frame = new System.Windows.Controls.Button { Content = "全体表示", Margin = new Thickness(8), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        frame.Click += (_, _) => _viewport.ZoomExtents(250);
        var toolbar = new DockPanel();
        DockPanel.SetDock(frame, Dock.Right);
        toolbar.Children.Add(frame);
        toolbar.Children.Add(_prefabs);

        _summary = new TextBlock { Foreground = System.Windows.Media.Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(9, 6, 9, 2), MaxHeight = 48 };
        _warnings = new TextBlock { Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 202, 110)), TextWrapping = TextWrapping.Wrap };
        var warningScroll = new ScrollViewer { Content = _warnings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 50, Margin = new Thickness(9, 2, 9, 7) };
        var footer = new System.Windows.Controls.StackPanel { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 43, 53)) };
        footer.Children.Add(_summary);
        footer.Children.Add(warningScroll);
        var root = new DockPanel { LastChildFill = true, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 29, 36)) };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(footer);
        root.Children.Add(center);
        _host = new ElementHost { Dock = System.Windows.Forms.DockStyle.Fill, Child = root };
    }

    public ElementHost Host => _host;
    public event Action<UnityPackageEntry>? CandidateSelected;

    public void ShowStatus(string summary, IEnumerable<string>? warnings = null)
    {
        _summary.Text = summary;
        _warnings.Text = string.Join("\r\n", (warnings ?? []).Take(4));
        _text.Text = summary;
    }

    public void ShowContentList(string summary, IReadOnlyList<string> items, int totalItems, IEnumerable<string>? warnings = null)
    {
        ShowStatus(summary, warnings);
        var lines = items.Take(24).Select(x => $"• {x}").ToList();
        if (totalItems > lines.Count) lines.Add($"ほか {totalItems - lines.Count} 件");
        _text.Text = lines.Count == 0 ? "表示できるアセットはありません。" : string.Join("\r\n", lines);
        _text.VerticalAlignment = VerticalAlignment.Top;
        _text.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
    }

    public void ShowPrepared(PreparedPreview prepared, string summary, string tempRoot, IReadOnlyList<string>? fallbackItems = null, int fallbackItemCount = 0)
    {
        ClearPrepared();
        _prepared = prepared;
        _preparedTempRoot = tempRoot;
        _models.Clear();
        foreach (var scene in prepared.Scenes) _models.AddNode(scene.Root);
        _viewport.Visibility = prepared.Scenes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        _text.Visibility = prepared.Scenes.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (prepared.Scenes.Count > 0)
        {
            var minimum = new System.Numerics.Vector3(float.PositiveInfinity);
            var maximum = new System.Numerics.Vector3(float.NegativeInfinity);
            var found = false;
            foreach (var scene in prepared.Scenes)
            {
                scene.Root.UpdateAllTransformMatrix();
                if (!scene.Root.TryGetBound(out var bounds)) continue;
                minimum = System.Numerics.Vector3.Min(minimum, bounds.Minimum);
                maximum = System.Numerics.Vector3.Max(maximum, bounds.Maximum);
                found = true;
            }
            if (found && _viewport.Camera is HelixToolkit.Wpf.SharpDX.PerspectiveCamera camera)
            {
                var center = (minimum + maximum) * 0.5f;
                var extent = MathF.Max((maximum - minimum).Length(), 1f);
                var direction = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(0.28f, 0.12f, 1f));
                var distance = extent * 1.45f;
                camera.Position = new System.Windows.Media.Media3D.Point3D(center.X + direction.X * distance, center.Y + direction.Y * distance, center.Z + direction.Z * distance);
                camera.LookDirection = new System.Windows.Media.Media3D.Vector3D(-direction.X * distance, -direction.Y * distance, -direction.Z * distance);
                camera.UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, 0);
                _viewport.FixedRotationPoint = new System.Windows.Media.Media3D.Point3D(center.X, center.Y, center.Z);
                _viewport.FixedRotationPointEnabled = true;
            }
            else
            {
                _viewport.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!_disposed) _viewport.ZoomExtents(250); }));
            }
        }
        if (prepared.Scenes.Count == 0)
        {
            var lines = (fallbackItems ?? []).Take(20).Select(x => $"• {x}").ToArray();
            var total = fallbackItemCount;
            if (total > lines.Length) lines = [.. lines, $"ほか {total - lines.Length} 件"];
            _text.Text = lines.Length == 0 ? "3Dモデルや画像は見つかりませんでした。" : "3Dモデルは見つかりませんでした。\r\n" + string.Join("\r\n", lines);
            _text.VerticalAlignment = VerticalAlignment.Top;
            _text.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        }
        _summary.Text = summary;
        _warnings.Text = string.Join("\r\n", prepared.Warnings.Take(4));
    }

    public void ShowImage(Bitmap bitmap, string summary, IEnumerable<string>? warnings = null)
    {
        ClearPrepared();
        _models.Clear();
        _viewport.Visibility = Visibility.Collapsed;
        _text.Visibility = Visibility.Collapsed;
        _image.Source = null;
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            _image.Source = source;
        }
        finally { DeleteObject(handle); bitmap.Dispose(); }
        _image.Visibility = Visibility.Visible;
        _summary.Text = summary;
        _warnings.Text = string.Join("\r\n", (warnings ?? []).Take(4));
    }

    public void SetCandidates(IEnumerable<UnityPackageEntry> candidates)
    {
        _candidateEntries = candidates.Take(512).ToArray();
        _prefabs.Items.Clear();
        foreach (var item in _candidateEntries) _prefabs.Items.Add(item.PathName);
        if (_prefabs.Items.Count == 0) _prefabs.Items.Add("Prefabなし");
        _prefabs.SelectedIndex = 0;
        _prefabs.IsEnabled = _prefabs.Items.Count > 1;
    }

    private void ClearPrepared()
    {
        _models.Clear();
        _prepared?.Dispose();
        _prepared = null;
        if (_preparedTempRoot is { } tempRoot)
        {
            _preparedTempRoot = null;
            PreviewHandlerCom.SafeDeleteOwnedTemp(tempRoot);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ClearPrepared();
        _viewport.EffectsManager = null;
        _effectsManager.Dispose();
        _host.Dispose();
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);
}

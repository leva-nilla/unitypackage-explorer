using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.Wpf.SharpDX;
using Microsoft.Win32;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.App;

public partial class MainWindow : Window
{
    private readonly string? _initialPath;
    private UnityAssetCatalog? _catalog;
    private IReadOnlyList<UnityPackageEntry> _entries = [];
    private string? _packagePath;
    private string? _directPrefabPath;
    private string? _temporaryRoot;
    private string _selectedKind = "すべて";
    private bool _updatingFilter;

    private sealed record TextureBinding(string MaterialName, string AssetPath);
    private sealed record ModelLoadResult(HelixToolkitScene Scene, IReadOnlyList<string> Warnings);

    public MainWindow(string? initialPath = null)
    {
        InitializeComponent();
        _initialPath = initialPath;
        KindFilter.ItemsSource = new[] { "すべて" };
        KindFilter.SelectedIndex = 0;
        Loaded += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_initialPath) && File.Exists(_initialPath)) await OpenPathAsync(_initialPath);
        };
        Closed += (_, _) => CleanupTemporaryFiles();
    }

    private async Task OpenPathAsync(string path)
    {
        try
        {
            SetBusy(true, "読み込み中…");
            ClearDisplay();
            _packagePath = null;
            _directPrefabPath = null;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".unitypackage")
            {
                _catalog = await Task.Run(() => UnityAssetCatalog.OpenPackage(path));
                _packagePath = Path.GetFullPath(path);
                _entries = _catalog.Entries;
                RefreshKindFilter();
                SourceLabel.Text = Path.GetFileName(path);
                ExtractButton.IsEnabled = true;
                DetailsText.Text = BuildSummary(_entries, Path.GetFileName(path));
                SetStatus($"{_entries.Count:N0} 個のパスを読み込みました。Unity は起動していません。");
                RefreshTree();
            }
            else if (extension == ".prefab")
            {
                _catalog = await Task.Run(() => UnityAssetCatalog.OpenProjectPrefab(path));
                _entries = _catalog.Entries;
                RefreshKindFilter();
                _directPrefabPath = Path.GetFullPath(path);
                SourceLabel.Text = Path.GetFileName(path);
                ExtractButton.IsEnabled = false;
                RefreshTree();
                var entry = _entries.FirstOrDefault(x => string.Equals(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_directPrefabPath)!, x.PathName)), _directPrefabPath, StringComparison.OrdinalIgnoreCase));
                await LoadPrefabAsync(entry, _directPrefabPath);
            }
            else
            {
                throw new InvalidDataException("対応形式は .unitypackage と .prefab です。");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"読み込みに失敗しました: {ex.Message}");
            DetailsText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string BuildSummary(IReadOnlyList<UnityPackageEntry> entries, string fileName)
    {
        var counts = entries.Where(x => x.HasAsset).GroupBy(x => x.Kind).OrderBy(x => x.Key)
            .Select(x => $"{x.Key}: {x.Count():N0}");
        var prefabNames = entries.Where(x => x.Extension == ".prefab").Select(x => Path.GetFileName(x.PathName)).Take(8).ToArray();
        var text = new StringBuilder()
            .AppendLine(fileName)
            .AppendLine($"Asset: {entries.Count(x => x.HasAsset):N0} 個")
            .AppendLine(string.Join("   ·   ", counts));
        if (prefabNames.Length > 0)
            text.AppendLine().AppendLine("Prefab").AppendLine(string.Join(Environment.NewLine, prefabNames));
        return text.ToString();
    }

    private void RefreshKindFilter()
    {
        var kinds = _entries.Where(x => x.HasAsset)
            .Select(x => x.Kind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _updatingFilter = true;
        try
        {
            KindFilter.ItemsSource = new[] { "すべて" }.Concat(kinds).ToArray();
            if (!_entries.Any(x => x.HasAsset && string.Equals(x.Kind, _selectedKind, StringComparison.OrdinalIgnoreCase)))
                _selectedKind = "すべて";
            KindFilter.SelectedItem = _selectedKind;
        }
        finally { _updatingFilter = false; }
    }

    private void RefreshTree()
    {
        _updatingFilter = true;
        try
        {
            var search = SearchBox.Text.Trim();
            var tree = UnityAssetCatalog.BuildTree(_entries, x => x.HasAsset || x.HasMeta);
            var filtered = UnityAssetCatalog.FilterTree(tree, search, _selectedKind)
                .Select(x => new AssetTreeItemView(x)).ToArray();
            AssetTree.ItemsSource = filtered;
        }
        finally { _updatingFilter = false; }
    }

    private async void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not AssetTreeItemView { Entry: { } entry } || !entry.HasAsset) return;
        if (_catalog is null) return;
        try
        {
            SetBusy(true, $"{entry.PathName} を読み込み中…");
            switch (entry.Extension)
            {
                case ".prefab": await LoadPrefabAsync(entry, null); break;
                case ".fbx": await LoadPackageModelAsync(entry, Matrix4x4.Identity, []); break;
                case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".gif": case ".tga": case ".psd": case ".tif": case ".tiff":
                    ShowImage(_catalog.ReadAsset(entry) ?? throw new InvalidDataException("画像データが見つかりません。"), entry.PathName);
                    break;
                default:
                    ClearDisplay();
                    DetailsText.Text = $"{entry.PathName}{Environment.NewLine}種類: {entry.Kind}{Environment.NewLine}GUID: {entry.Guid}";
                    SetStatus("このファイル形式は一覧で確認できます。画像・Prefab・FBX はプレビューできます。");
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus($"表示に失敗しました: {ex.Message}");
            DetailsText.Text = ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async Task LoadPrefabAsync(UnityPackageEntry? entry, string? externalPath)
    {
        if (_catalog is null) return;
        ClearDisplay();
        byte[]? bytes = externalPath is null ? entry is null ? null : _catalog.ReadAsset(entry) : await File.ReadAllBytesAsync(externalPath);
        if (bytes is null) throw new InvalidDataException("Prefab の内容を読み取れません。");
        var result = UnityPrefabParser.Parse(Encoding.UTF8.GetString(bytes));
        PrefabTree.ItemsSource = result.Roots.Select(x => new PrefabNodeView(x)).ToArray();
        var warnings = new List<string>(result.Warnings);
        var placements = new List<(PrefabNode Node, string Guid, Matrix4x4 Transform)>();
        foreach (var root in result.Roots) CollectPlacements(root, Matrix4x4.Identity, placements);

        var models = new List<HelixToolkitScene>();
        foreach (var (node, guid, transform) in placements)
        {
            var modelPath = _catalog.ResolvePath(guid);
            if (modelPath is null)
            {
                warnings.Add($"{node.Name}: 参照 GUID {guid} の素材が見つかりません。");
                continue;
            }
            if (!modelPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{node.Name}: FBX 以外の参照は 3D 表示できません ({modelPath})。");
                continue;
            }
            try
            {
                var loaded = await LoadPackageModelAsyncCore(guid, transform, node.Materials);
                models.Add(loaded.Scene);
                SceneModels.AddNode(loaded.Scene.Root);
                warnings.AddRange(loaded.Warnings.Select(x => $"{node.Name}: {x}"));
            }
            catch (Exception ex) { warnings.Add($"{node.Name}: {ex.Message}"); }
        }

        var details = new StringBuilder()
            .AppendLine(externalPath ?? entry?.PathName ?? "Prefab")
            .AppendLine($"Prefab 階層: {CountNodes(result.Roots):N0} 個")
            .AppendLine($"FBX 表示: {models.Count:N0} 個");
        if (warnings.Count > 0) details.AppendLine().AppendLine("確認できない内容").AppendLine(string.Join(Environment.NewLine, warnings.Distinct()));
        if (models.Count > 0) Viewport.ZoomExtents();
        else ViewerEmpty.Text = "FBX 参照がありません。Prefab の階層を表示しています";
        DetailsText.Text = details.ToString();
        SetStatus(models.Count > 0 ? "参照 FBX と確認できたテクスチャを表示しています。シェーダーは使いません。" : "Prefab の構造を読み込みました。");
    }

    private async Task LoadPackageModelAsync(UnityPackageEntry entry, Matrix4x4 transform, IReadOnlyList<UnityAssetReference> materials)
    {
        var loaded = await LoadPackageModelAsyncCore(entry.Guid, transform, materials);
        ClearSceneModels();
        SceneModels.AddNode(loaded.Scene.Root);
        Viewport.ZoomExtents();
        PrefabTree.ItemsSource = null;
        DetailsText.Text = $"{entry.PathName}{Environment.NewLine}種類: Model{Environment.NewLine}GUID: {entry.Guid}";
        if (loaded.Warnings.Count > 0) DetailsText.Text += Environment.NewLine + Environment.NewLine + "確認できない内容" + Environment.NewLine + string.Join(Environment.NewLine, loaded.Warnings);
        SetStatus(loaded.Warnings.Count == 0 ? "FBX を表示しています。" : "FBX を表示しました。一部の参照は確認できませんでした。");
    }

    private async Task<ModelLoadResult> LoadPackageModelAsyncCore(string guid, Matrix4x4 transform, IReadOnlyList<UnityAssetReference> materials)
    {
        if (_catalog is null) throw new InvalidOperationException("Asset catalog is not open.");
        var temp = EnsureTemporaryRoot();
        var modelPath = _catalog.StageAsset(guid, temp);
        StageSiblingImages(guid);
        var textureBindings = new List<TextureBinding>();
        var warnings = new List<string>();
        foreach (var material in materials)
        {
            if (material.Guid is null)
            {
                if (material.FileId != 0) warnings.Add($"Material fileID {material.FileId} has no GUID and cannot be resolved.");
                continue;
            }

            var materialPath = _catalog.ResolvePath(material.Guid);
            if (materialPath is null)
            {
                warnings.Add($"Material GUID {material.Guid} was not found.");
                continue;
            }
            if (_catalog.ReadAssetByGuid(material.Guid) is null)
            {
                warnings.Add($"Material data is missing: {materialPath}");
                continue;
            }

            var materialName = _catalog.ReadMaterialName(material.Guid) ?? Path.GetFileNameWithoutExtension(materialPath);
            var materialTextureGuids = _catalog.ReadMaterialTextureGuids(material.Guid);
            foreach (var textureGuid in _catalog.ReadUnsupportedMaterialTextureGuids(material.Guid))
            {
                var unsupportedPath = _catalog.ResolvePath(textureGuid);
                warnings.Add(unsupportedPath is null
                    ? $"Texture GUID {textureGuid} referenced by material '{materialName}' was not found."
                    : $"Texture {unsupportedPath} is referenced by a material slot that this simplified viewer does not render.");
            }
            foreach (var textureGuid in materialTextureGuids)
            {
                var textureSourcePath = _catalog.ResolvePath(textureGuid);
                if (textureSourcePath is null)
                {
                    warnings.Add($"Texture GUID {textureGuid} referenced by material '{materialName}' was not found.");
                    continue;
                }

                var textureExtension = Path.GetExtension(textureSourcePath);
                if (textureExtension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
                    || !new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".tga" }.Contains(textureExtension, StringComparer.OrdinalIgnoreCase))
                {
                    warnings.Add($"Texture format is not supported for 3D display: {textureSourcePath}");
                    continue;
                }

                var texturePath = _catalog.StageImage(textureGuid, temp);
                if (texturePath is null)
                {
                    warnings.Add($"Texture data is missing: {textureSourcePath}");
                    continue;
                }
                if (textureExtension.Equals(".tga", StringComparison.OrdinalIgnoreCase))
                    texturePath = ConvertTgaToPng(texturePath);
                textureBindings.Add(new TextureBinding(materialName, texturePath));
            }
        }

        return await Task.Run(() =>
        {
            using var importer = new Importer();
            var configuration = new ImporterConfiguration { ImportMaterialType = MaterialType.BlinnPhong };
            var scene = importer.Load(modelPath, configuration);
            if (scene is null) throw new InvalidDataException("FBX を読み込めませんでした。");
            scene.Root.ModelMatrix *= transform;
            warnings.AddRange(ApplyUnityTextures(scene.Root, textureBindings, materials));
            return new ModelLoadResult(scene, warnings);
        });
    }

    private void StageSiblingImages(string modelGuid)
    {
        if (_catalog is null) return;
        var modelEntry = _catalog.Entries.FirstOrDefault(x => string.Equals(x.Guid, modelGuid, StringComparison.OrdinalIgnoreCase));
        if (modelEntry is null) return;
        var sourceDirectory = Path.GetDirectoryName(modelEntry.PathName.Replace('/', Path.DirectorySeparatorChar));
        foreach (var image in _catalog.Entries.Where(x => x.Kind == "Image" && string.Equals(Path.GetDirectoryName(x.PathName.Replace('/', Path.DirectorySeparatorChar)), sourceDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            try { _catalog.StageAsset(image.Guid, _temporaryRoot!); }
            catch (Exception) { }
        }
    }

    private static IReadOnlyList<string> ApplyUnityTextures(
        HelixToolkit.SharpDX.Model.Scene.SceneNode root,
        IReadOnlyList<TextureBinding> textureBindings,
        IReadOnlyList<UnityAssetReference> materialReferences)
    {
        if (textureBindings.Count == 0) return [];
        var meshNodes = EnumerateSceneNodes(root).OfType<HelixToolkit.SharpDX.Model.Scene.MeshNode>().ToArray();
        var warnings = new List<string>();
        var applied = new HashSet<TextureBinding>();
        foreach (var mesh in meshNodes)
        {
            var matching = textureBindings.Where(x => string.Equals(x.MaterialName, mesh.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var paths = matching.Select(x => x.AssetPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length == 1)
            {
                SetDiffuseTexture(mesh, paths[0]);
                foreach (var item in matching) applied.Add(item);
            }
            else if (paths.Length > 1)
                warnings.Add($"Several textures match FBX material '{mesh.Name}'; no texture was selected automatically.");
        }

        var allReferencedMaterialsHaveOneTexture = materialReferences.Count == 1
            && textureBindings.Select(x => x.AssetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        if (applied.Count == 0 && allReferencedMaterialsHaveOneTexture)
        {
            var path = textureBindings[0].AssetPath;
            foreach (var mesh in meshNodes) SetDiffuseTexture(mesh, path);
            foreach (var item in textureBindings) applied.Add(item);
        }

        foreach (var binding in textureBindings.Except(applied))
            warnings.Add($"Texture for material '{binding.MaterialName}' could not be matched to an FBX mesh and was left unused.");
        return warnings;
    }

    private static void SetDiffuseTexture(HelixToolkit.SharpDX.Model.Scene.MeshNode mesh, string path) =>
        mesh.Material = new HelixToolkit.SharpDX.Model.PhongMaterialCore
        {
            DiffuseColor = new HelixToolkit.Maths.Color4(1, 1, 1, 1),
            DiffuseMap = HelixToolkit.SharpDX.TextureModel.Create(path)
        };

    private static IEnumerable<HelixToolkit.SharpDX.Model.Scene.SceneNode> EnumerateSceneNodes(HelixToolkit.SharpDX.Model.Scene.SceneNode root)
    {
        yield return root;
        foreach (var child in root.Items)
            foreach (var nested in EnumerateSceneNodes(child)) yield return nested;
    }

    private void CollectPlacements(PrefabNode node, Matrix4x4 parent, List<(PrefabNode, string, Matrix4x4)> output)
    {
        var local = Matrix4x4.CreateScale(node.Scale.X, node.Scale.Y, node.Scale.Z)
            * Matrix4x4.CreateFromQuaternion(new Quaternion(node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W))
            * Matrix4x4.CreateTranslation(node.Position.X, node.Position.Y, node.Position.Z);
        var world = local * parent;
        var guid = node.Mesh?.Guid ?? node.SourcePrefab?.Guid;
        if (!string.IsNullOrWhiteSpace(guid)) output.Add((node, guid, world));
        foreach (var child in node.Children) CollectPlacements(child, world, output);
    }

    private static int CountNodes(IEnumerable<PrefabNode> nodes) => nodes.Sum(x => 1 + CountNodes(x.Children));

    private void ShowImage(byte[] bytes, string pathName)
    {
        ClearSceneModels();
        BitmapSource bitmap;
        if (Path.GetExtension(pathName).Equals(".tga", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = TgaDecoder.Decode(bytes);
            bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra32Pixels, decoded.Width * 4);
        }
        else if (Path.GetExtension(pathName).Equals(".psd", StringComparison.OrdinalIgnoreCase))
        {
            ClearDisplay();
            DetailsText.Text = $"{pathName}{Environment.NewLine}PSD 画像のプレビューには対応していません。PNG、JPEG、BMP、GIF、TGA、TIFF に変換して確認してください。";
            SetStatus("PSD は一覧に表示できますが、画像プレビューには対応していません。");
            return;
        }
        else
        {
            var decoded = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            decoded.BeginInit();
            decoded.CacheOption = BitmapCacheOption.OnLoad;
            decoded.StreamSource = stream;
            decoded.EndInit();
            bitmap = decoded;
        }
        bitmap.Freeze();
        AssetImage.Source = bitmap;
        ImagePanel.Visibility = Visibility.Visible;
        Viewport.Visibility = Visibility.Collapsed;
        ViewerEmpty.Visibility = Visibility.Collapsed;
        PrefabTree.ItemsSource = null;
        DetailsText.Text = $"{pathName}{Environment.NewLine}{bitmap.PixelWidth} × {bitmap.PixelHeight}";
        SetStatus("画像を表示しています。");
    }

    private void ClearDisplay()
    {
        ClearSceneModels();
        AssetImage.Source = null;
        ImagePanel.Visibility = Visibility.Collapsed;
        Viewport.Visibility = Visibility.Visible;
        ViewerEmpty.Visibility = Visibility.Visible;
        ViewerEmpty.Text = "FBX・Prefab・画像を選択すると、ここに表示される";
        PrefabTree.ItemsSource = null;
    }

    private void ClearSceneModels()
    {
        SceneModels.Clear();
        Viewport.Visibility = Visibility.Visible;
        ImagePanel.Visibility = Visibility.Collapsed;
        ViewerEmpty.Visibility = Visibility.Visible;
    }

    private string EnsureTemporaryRoot()
    {
        if (_temporaryRoot is not null && Directory.Exists(_temporaryRoot)) return _temporaryRoot;
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "UnityPackageExplorer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
        return _temporaryRoot;
    }

    private string ConvertTgaToPng(string tgaPath)
    {
        var decoded = TgaDecoder.Decode(File.ReadAllBytes(tgaPath));
        var bitmap = BitmapSource.Create(decoded.Width, decoded.Height, 96, 96, PixelFormats.Bgra32, null, decoded.Bgra32Pixels, decoded.Width * 4);
        bitmap.Freeze();
        var pngPath = Path.ChangeExtension(tgaPath, ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(pngPath);
        encoder.Save(stream);
        return pngPath;
    }

    private void CleanupTemporaryFiles()
    {
        if (_temporaryRoot is not null && Directory.Exists(_temporaryRoot))
        {
            try { Directory.Delete(_temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        OpenButton.IsEnabled = !busy;
        ExtractButton.IsEnabled = !busy && _packagePath is not null;
        AssetTree.IsEnabled = !busy;
        if (message is not null) SetStatus(message);
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private void SetStatus(string message) => StatusLabel.Text = message;

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Unity アセットを選択",
            Filter = "UnityPackage / Prefab|*.unitypackage;*.prefab|UnityPackage|*.unitypackage|Prefab|*.prefab|すべてのファイル|*.*"
        };
        if (dialog.ShowDialog(this) == true) await OpenPathAsync(dialog.FileName);
    }

    private void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_packagePath is null) return;
        var dialog = new OpenFolderDialog { Title = "展開先フォルダーを選択", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var target = Path.Combine(dialog.FolderName, Path.GetFileNameWithoutExtension(_packagePath) + "_extracted");
            var output = new UnityPackageReader(_packagePath).ExtractTo(target);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{output}\"") { UseShellExecute = true });
            SetStatus($"展開しました: {output}");
        }
        catch (Exception ex)
        {
            SetStatus($"展開に失敗しました: {ex.Message}");
            MessageBox.Show(this, ex.Message, "UnityPackage Explorer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_updatingFilter) RefreshTree();
    }

    private void KindFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (KindFilter.SelectedItem is string kind) _selectedKind = kind;
        if (!_updatingFilter && IsLoaded) RefreshTree();
    }
}

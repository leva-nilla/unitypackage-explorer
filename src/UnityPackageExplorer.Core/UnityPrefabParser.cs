using System.Globalization;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

/// <summary>Reads the Unity text-serialized Prefab subset used for hierarchy and model references.</summary>
public static partial class UnityPrefabParser
{
    private static readonly Regex DocumentHeader = new("(?m)^--- !u!(?<class>\\d+) &(?<id>-?\\d+)[^\\r\\n]*", RegexOptions.Compiled);
    private static readonly Regex FileReference = new("\\{\\s*fileID:\\s*(-?\\d+)(?:\\s*,\\s*guid:\\s*([0-9a-fA-F]{32}))?", RegexOptions.Compiled);

    public static PrefabParseResult Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var warnings = new List<string>();
        var docs = SplitDocuments(yaml);
        var objects = new Dictionary<long, GameObjectData>();
        var transforms = new Dictionary<long, TransformData>();
        var components = new Dictionary<long, ComponentData>();
        var prefabInstances = new Dictionary<long, PrefabInstanceData>();

        foreach (var document in docs)
        {
            switch (document.ClassId)
            {
                case 1:
                    objects[document.FileId] = new GameObjectData(
                        document.FileId,
                        ReadScalar(document.Body, "m_Name") ?? "GameObject",
                        ReadReferences(document.Body, "m_Component", null).Select(x => x.FileId).ToArray(),
                        ReadSingleReference(document.Body, "m_PrefabInstance")?.FileId ?? 0);
                    break;
                case 4:
                case 224:
                    var gameObject = ReadSingleReference(document.Body, "m_GameObject")?.FileId ?? 0;
                    transforms[document.FileId] = new TransformData(
                        document.FileId,
                        gameObject,
                        ReadSingleReference(document.Body, "m_Father")?.FileId ?? 0,
                        ReadVector(document.Body, "m_LocalPosition", Vector3Value.Zero),
                        ReadQuaternion(document.Body),
                        ReadVector(document.Body, "m_LocalScale", Vector3Value.One));
                    break;
                case 1001:
                    var source = ReadSingleReference(document.Body, "m_SourcePrefab");
                    if (source is not null)
                    {
                        var parent = ReadSingleReference(document.Body, "m_TransformParent")?.FileId ?? 0;
                        prefabInstances[document.FileId] = new PrefabInstanceData(document.FileId, source, parent);
                    }
                    break;
                default:
                    components[document.FileId] = new ComponentData(document.FileId, document.ClassId, document.Body);
                    break;
            }
        }

        var transformByGameObject = transforms.Values
            .Where(x => x.GameObjectFileId != 0)
            .GroupBy(x => x.GameObjectFileId)
            .ToDictionary(x => x.Key, x => x.First());
        var transformIdToGameObject = transforms.Values.ToDictionary(x => x.FileId, x => x.GameObjectFileId);
        var nodes = new Dictionary<long, PrefabNode>();

        foreach (var gameObject in objects.Values)
        {
            transformByGameObject.TryGetValue(gameObject.FileId, out var transform);
            var node = new PrefabNode { FileId = gameObject.FileId, Name = gameObject.Name };
            if (transform is not null)
            {
                node.ParentFileId = transform.FatherFileId != 0 && transformIdToGameObject.TryGetValue(transform.FatherFileId, out var parentId) ? parentId : 0;
                node.Position = transform.Position;
                node.Rotation = transform.Rotation;
                node.Scale = transform.Scale;
            }

            foreach (var componentId in gameObject.ComponentFileIds)
            {
                if (!components.TryGetValue(componentId, out var component))
                {
                    node.Components.Add("Unknown component");
                    continue;
                }

                var componentName = GetClassName(component.ClassId);
                node.Components.Add(componentName);
                if (component.ClassId is 33 or 137)
                {
                    node.Mesh = ReadSingleReference(component.Body, "m_Mesh");
                    if (node.Mesh is { FileId: not 0, Guid: null })
                        warnings.Add($"'{node.Name}' has a mesh reference without a GUID; it cannot be resolved from the available assets.");
                }
                if (component.ClassId is 23 or 137)
                {
                    node.Materials.AddRange(ReadReferences(component.Body, "m_Materials", "m_Bits"));
                    if (node.Materials.Any(x => x.FileId != 0 && x.Guid is null))
                        warnings.Add($"'{node.Name}' has a material reference without a GUID; its texture cannot be resolved from the available assets.");
                }
            }

            if (gameObject.PrefabInstanceId != 0 && prefabInstances.TryGetValue(gameObject.PrefabInstanceId, out var instance))
                node.SourcePrefab = instance.SourcePrefab;
            nodes[node.FileId] = node;
        }

        // A prefab instance may be represented only by a stripped Transform/GameObject pair.
        foreach (var instance in prefabInstances.Values)
        {
            if (objects.Values.Any(x => x.PrefabInstanceId == instance.FileId)) continue;
            var parentId = instance.ParentTransformFileId != 0 && transformIdToGameObject.TryGetValue(instance.ParentTransformFileId, out var parent) ? parent : 0;
            nodes[instance.FileId] = new PrefabNode
            {
                FileId = instance.FileId,
                Name = $"Prefab ({instance.SourcePrefab.Guid ?? instance.SourcePrefab.FileId.ToString(CultureInfo.InvariantCulture)})",
                ParentFileId = parentId,
                SourcePrefab = instance.SourcePrefab
            };
        }

        foreach (var node in nodes.Values)
        {
            if (node.ParentFileId == 0) continue;
            if (!nodes.TryGetValue(node.ParentFileId, out var parentNode))
            {
                warnings.Add($"Parent object {node.ParentFileId} was not found for '{node.Name}'.");
                node.ParentFileId = 0;
                continue;
            }
            parentNode.Children.Add(node);
        }

        var roots = nodes.Values.Where(x => x.ParentFileId == 0).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0 && nodes.Count > 0)
        {
            warnings.Add("Prefab hierarchy contains a cycle or unresolved parent chain.");
            roots = nodes.Values.Take(1).ToArray();
        }
        if (docs.Count == 0) warnings.Add("No Unity text-serialized Prefab documents were found.");
        return new PrefabParseResult(roots, warnings);
    }

    public static string GetClassName(int classId) => classId switch
    {
        1 => "GameObject",
        4 => "Transform",
        20 => "Camera",
        23 => "MeshRenderer",
        33 => "MeshFilter",
        54 => "Rigidbody",
        65 => "BoxCollider",
        108 => "Light",
        114 => "MonoBehaviour",
        135 => "SphereCollider",
        137 => "SkinnedMeshRenderer",
        224 => "RectTransform",
        1001 => "PrefabInstance",
        _ => $"Unity component {classId}"
    };

    private static List<YamlDocument> SplitDocuments(string text)
    {
        var matches = DocumentHeader.Matches(text);
        var result = new List<YamlDocument>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var bodyStart = matches[i].Index + matches[i].Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            result.Add(new YamlDocument(
                int.Parse(matches[i].Groups["class"].Value, CultureInfo.InvariantCulture),
                long.Parse(matches[i].Groups["id"].Value, CultureInfo.InvariantCulture),
                text[bodyStart..bodyEnd]));
        }
        return result;
    }

    private static string? ReadScalar(string body, string key)
    {
        var match = Regex.Match(body, $"(?m)^\\s*{Regex.Escape(key)}:\\s*(?<value>[^\\r\\n]*)");
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"', '\'') : null;
    }

    private static UnityAssetReference? ReadSingleReference(string body, string key)
    {
        var match = Regex.Match(body, $"(?m)^\\s*{Regex.Escape(key)}:\\s*(?<ref>\\{{[^\\r\\n]*\\}})");
        if (!match.Success) return null;
        return ParseReference(match.Groups["ref"].Value);
    }

    private static IEnumerable<UnityAssetReference> ReadReferences(string body, string sectionName, string? stopAt)
    {
        var section = Regex.Match(body, $"(?ms)^\\s*{Regex.Escape(sectionName)}:\\s*(?<section>.*?)(?=^\\s*\\w[\\w ]*:\\s|\\z)");
        if (!section.Success) yield break;
        foreach (Match match in FileReference.Matches(section.Groups["section"].Value))
        {
            if (stopAt is not null && section.Groups["section"].Value.AsSpan(match.Index).StartsWith(stopAt, StringComparison.Ordinal)) yield break;
            yield return ParseReference(match.Value)!;
        }
    }

    private static UnityAssetReference? ParseReference(string text)
    {
        var match = FileReference.Match(text);
        if (!match.Success) return null;
        return new UnityAssetReference(
            long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : null);
    }

    private static Vector3Value ReadVector(string body, string key, Vector3Value fallback)
    {
        var match = Regex.Match(body, $"(?ms)^\\s*{Regex.Escape(key)}:\\s*\\{{(?<value>.*?)\\}}");
        if (!match.Success) return fallback;
        return new Vector3Value(ReadFloat(match.Groups["value"].Value, "x", fallback.X), ReadFloat(match.Groups["value"].Value, "y", fallback.Y), ReadFloat(match.Groups["value"].Value, "z", fallback.Z));
    }

    private static QuaternionValue ReadQuaternion(string body)
    {
        var match = Regex.Match(body, "(?ms)^\\s*m_LocalRotation:\\s*\\{(?<value>.*?)\\}");
        if (!match.Success) return QuaternionValue.Identity;
        var value = match.Groups["value"].Value;
        return new QuaternionValue(ReadFloat(value, "x", 0), ReadFloat(value, "y", 0), ReadFloat(value, "z", 0), ReadFloat(value, "w", 1));
    }

    private static float ReadFloat(string text, string key, float fallback)
    {
        var match = Regex.Match(text, $"(?:^|,)\\s*{key}:\\s*(-?(?:[0-9]+\\.?[0-9]*|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?)");
        return match.Success && float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private sealed record YamlDocument(int ClassId, long FileId, string Body);
    private sealed record GameObjectData(long FileId, string Name, long[] ComponentFileIds, long PrefabInstanceId);
    private sealed record TransformData(long FileId, long GameObjectFileId, long FatherFileId, Vector3Value Position, QuaternionValue Rotation, Vector3Value Scale);
    private sealed record ComponentData(long FileId, int ClassId, string Body);
    private sealed record PrefabInstanceData(long FileId, UnityAssetReference SourcePrefab, long ParentTransformFileId);
}

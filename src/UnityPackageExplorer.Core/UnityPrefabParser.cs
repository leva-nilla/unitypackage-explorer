using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

/// <summary>Reads the Unity text-serialized Prefab subset used for hierarchy and model references.</summary>
public static partial class UnityPrefabParser
{
    private const int MaximumHierarchyDepth = 256;
    public const int DefaultMaximumNestedPrefabDepth = 32;
    public const int MaximumResolvedPrefabFileBytes = 32 * 1024 * 1024;
    public const int MaximumResolvedPrefabBytes = 64 * 1024 * 1024;
    public const int MaximumResolvedPrefabNodes = 100_000;
    private static readonly Regex DocumentHeader = new("(?m)^--- !u!(?<class>\\d+) &(?<id>-?\\d+)[^\\r\\n]*", RegexOptions.Compiled);
    private static readonly Regex FileReference = new("\\{\\s*fileID:\\s*(-?\\d+)(?:\\s*,\\s*guid:\\s*([0-9a-fA-F]{32}))?", RegexOptions.Compiled);

    public static PrefabParseResult Parse(string yaml, Func<string, long?>? rootTransformLookup = null)
        => ParseCore(yaml, rootTransformLookup, CancellationToken.None);

    private static PrefabParseResult ParseCore(string yaml, Func<string, long?>? rootTransformLookup, CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
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
                        var modifications = ReadPrefabTransformOverrides(document.Body);
                        var materialOverrides = ReadPrefabMaterialOverrides(document.Body);
                        var rootTransformFileId = source.Guid is null ? null : rootTransformLookup?.Invoke(source.Guid);
                        var transformOverrides = SelectRootTransformOverrides(modifications, source.Guid, rootTransformFileId, warnings);
                        prefabInstances[document.FileId] = new PrefabInstanceData(document.FileId, source, parent, transformOverrides, materialOverrides, rootTransformFileId);
                        if (source.Guid is null)
                            warnings.Add($"PrefabInstance {document.FileId} has no source asset GUID and cannot be expanded.");
                    }
                    else warnings.Add($"PrefabInstance {document.FileId} has no readable m_SourcePrefab reference.");
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
            cancellationToken.ThrowIfCancellationRequested();
            transformByGameObject.TryGetValue(gameObject.FileId, out var transform);
            var node = new PrefabNode { FileId = gameObject.FileId, Name = gameObject.Name, TransformFileId = transform?.FileId ?? 0 };
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
                    if (transforms.ContainsKey(componentId)) continue;
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
                    node.RendererComponentFileIds.Add(componentId);
                    node.Materials.AddRange(ReadReferences(component.Body, "m_Materials", "m_Bits"));
                    if (node.Materials.Any(x => x.FileId != 0 && x.Guid is null))
                        warnings.Add($"'{node.Name}' has a material reference without a GUID; its texture cannot be resolved from the available assets.");
                }
            }

            if (gameObject.PrefabInstanceId != 0 && prefabInstances.TryGetValue(gameObject.PrefabInstanceId, out var instance))
            {
                node.SourcePrefab = instance.SourcePrefab;
                node.MaterialOverrides.AddRange(instance.MaterialOverrides);
                node.HasRootTransformOverrides = instance.TransformOverrides.Count > 0;
                node.RootTransformTargetFileId = instance.RootTransformFileId;
                node.RootTransformOverrides = instance.TransformOverrides;
                if (instance.MaterialOverrides.Count > 0)
                    warnings.Add($"'{node.Name}' has {instance.MaterialOverrides.Count} nested Prefab material override(s); target renderer and slot are retained for diagnostics but cannot be mapped safely to imported FBX submeshes.");
                if (transform is not null && transform.FatherFileId == instance.ParentTransformFileId)
                {
                    node.Position = ReadOverrideVector(instance.TransformOverrides, "m_LocalPosition", node.Position);
                    node.Rotation = ReadOverrideQuaternion(instance.TransformOverrides, node.Rotation);
                    node.Scale = ReadOverrideVector(instance.TransformOverrides, "m_LocalScale", node.Scale);
                }
            }
            nodes[node.FileId] = node;
        }

        // A prefab instance may be represented only by a stripped Transform/GameObject pair.
        foreach (var instance in prefabInstances.Values)
        {
            var hasRootObject = objects.Values.Any(x =>
                x.PrefabInstanceId == instance.FileId
                && transformByGameObject.TryGetValue(x.FileId, out var transform)
                && transform.FatherFileId == instance.ParentTransformFileId);
            if (hasRootObject) continue;
            var parentId = instance.ParentTransformFileId != 0 && transformIdToGameObject.TryGetValue(instance.ParentTransformFileId, out var parent) ? parent : 0;
            var instanceNode = new PrefabNode
            {
                FileId = instance.FileId,
                Name = $"Prefab ({instance.SourcePrefab.Guid ?? instance.SourcePrefab.FileId.ToString(CultureInfo.InvariantCulture)})",
                ParentFileId = parentId,
                Position = ReadOverrideVector(instance.TransformOverrides, "m_LocalPosition", Vector3Value.Zero),
                Rotation = ReadOverrideQuaternion(instance.TransformOverrides, QuaternionValue.Identity),
                Scale = ReadOverrideVector(instance.TransformOverrides, "m_LocalScale", Vector3Value.One),
                SourcePrefab = instance.SourcePrefab
            };
            instanceNode.HasRootTransformOverrides = instance.TransformOverrides.Count > 0;
            instanceNode.RootTransformTargetFileId = instance.RootTransformFileId;
            instanceNode.RootTransformOverrides = instance.TransformOverrides;
            instanceNode.MaterialOverrides.AddRange(instance.MaterialOverrides);
            if (instance.MaterialOverrides.Count > 0)
                warnings.Add($"'{instanceNode.Name}' has {instance.MaterialOverrides.Count} nested Prefab material override(s); target renderer and slot are retained for diagnostics but cannot be mapped safely to imported FBX submeshes.");
            nodes[instance.FileId] = instanceNode;
        }

        BreakParentCyclesAndExcessiveDepth(nodes, warnings);

        foreach (var node in nodes.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
        if (docs.Count == 0) warnings.Add("No Unity text-serialized Prefab documents were found.");
        return new PrefabParseResult(roots, warnings);
    }

    /// <summary>Parses a Prefab and expands nested Prefab instances using bounded, caller-provided asset resolution.</summary>
    public static PrefabParseResult ParseResolved(
        string yaml,
        string? sourceGuid,
        Func<string, CancellationToken, string?> resolvePrefabYaml,
        CancellationToken cancellationToken = default,
        int maximumNestedDepth = DefaultMaximumNestedPrefabDepth)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(resolvePrefabYaml);
        if (maximumNestedDepth < 0) throw new ArgumentOutOfRangeException(nameof(maximumNestedDepth));

        var warnings = new List<string>();
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var resolverFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalBytes = 0;
        var totalNodes = 0;
        var rootBytes = System.Text.Encoding.UTF8.GetByteCount(yaml);
        if (rootBytes > MaximumResolvedPrefabFileBytes || rootBytes > MaximumResolvedPrefabBytes)
            warnings.Add($"Prefab text exceeds the {MaximumResolvedPrefabFileBytes / (1024 * 1024)} MiB per-file resolution limit; nested Prefabs were not expanded.");
        totalBytes = Math.Min(rootBytes, MaximumResolvedPrefabBytes);

        string? ResolveYaml(string guid, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (cache.TryGetValue(guid, out var cached)) return cached;
            string? resolved;
            try { resolved = resolvePrefabYaml(guid, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                resolverFailures.Add(guid);
                warnings.Add($"Nested Prefab {guid} could not be read: {exception.Message}");
                cache[guid] = null;
                return null;
            }
            token.ThrowIfCancellationRequested();
            if (resolved is null) { cache[guid] = null; return null; }
            var bytes = System.Text.Encoding.UTF8.GetByteCount(resolved);
            if (bytes > MaximumResolvedPrefabFileBytes)
            {
                warnings.Add($"Nested Prefab {guid} exceeds the {MaximumResolvedPrefabFileBytes / (1024 * 1024)} MiB per-file limit and was not expanded.");
                cache[guid] = null;
                return null;
            }
            if ((long)totalBytes + bytes > MaximumResolvedPrefabBytes)
            {
                warnings.Add($"Nested Prefab expansion exceeds the {MaximumResolvedPrefabBytes / (1024 * 1024)} MiB total text budget; remaining instances were not expanded.");
                cache[guid] = null;
                return null;
            }
            totalBytes += bytes;
            cache[guid] = resolved;
            return resolved;
        }

        long? RootTransformLookup(string guid)
        {
            var source = ResolveYaml(guid, cancellationToken);
            return source is null ? null : FindRootTransformFileId(source);
        }

        var parsed = ParseCore(yaml, RootTransformLookup, cancellationToken);
        warnings.AddRange(parsed.Warnings.Where(x => !x.Contains("target renderer and slot are retained for diagnostics", StringComparison.Ordinal)));
        totalNodes = CountNodes(parsed.Roots);
        if (rootBytes > MaximumResolvedPrefabFileBytes || rootBytes > MaximumResolvedPrefabBytes)
            return new PrefabParseResult(parsed.Roots, warnings);
        if (totalNodes > MaximumResolvedPrefabNodes)
        {
            warnings.Add($"Prefab hierarchy exceeds the {MaximumResolvedPrefabNodes:N0} node resolution budget; nested Prefabs were not expanded.");
            return new PrefabParseResult(parsed.Roots, warnings);
        }
        var ancestry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(sourceGuid)) ancestry.Add(sourceGuid);
        AddDirectRendererIdentities(parsed.Roots, sourceGuid);
        ExpandInstances(parsed.Roots, sourceGuid, 0, 0, ancestry);
        return new PrefabParseResult(parsed.Roots, warnings.Distinct(StringComparer.Ordinal).ToArray());

        void ExpandInstances(IReadOnlyList<PrefabNode> roots, string? containingGuid, int depth, int hierarchyDepth, HashSet<string> activeGuids)
        {
            foreach (var node in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hierarchyDepth >= MaximumHierarchyDepth - 1)
                {
                    if (node.Children.Count > 0)
                    {
                        node.Children.Clear();
                        warnings.Add($"Resolved Prefab hierarchy reached the {MaximumHierarchyDepth}-level combined depth budget at '{node.Name}'; deeper children were omitted.");
                    }
                }
                else ExpandInstances(node.Children, containingGuid, depth, hierarchyDepth + 1, activeGuids);
                if (node.SourcePrefab?.Guid is { Length: > 0 } referencedGuid)
                {
                    if (depth >= maximumNestedDepth)
                    {
                        warnings.Add($"Nested Prefab expansion reached the configured depth limit ({maximumNestedDepth}) at {referencedGuid}.");
                    }
                    else if (activeGuids.Contains(referencedGuid))
                    {
                        warnings.Add($"Nested Prefab cycle detected at {referencedGuid}; this instance was left as a reference.");
                    }
                    else if (ResolveYaml(referencedGuid, cancellationToken) is { } sourceText)
                    {
                        var sourceParse = ParseCore(sourceText, RootTransformLookup, cancellationToken);
                        var sourceNodeCount = CountNodes(sourceParse.Roots);
                        if ((long)totalNodes + sourceNodeCount > MaximumResolvedPrefabNodes)
                        {
                            warnings.Add($"Nested Prefab expansion exceeds the {MaximumResolvedPrefabNodes:N0} node budget at {referencedGuid}; this instance was left as a reference.");
                        }
                        else
                        {
                            totalNodes += sourceNodeCount;
                            warnings.AddRange(sourceParse.Warnings.Where(x => !x.Contains("target renderer and slot are retained for diagnostics", StringComparison.Ordinal)));
                            var isSingleRoot = sourceParse.Roots.Count == 1;
                            var sourceRootDepth = hierarchyDepth + (isSingleRoot ? 0 : 1);
                            var maximumRelativeDepth = MaximumHierarchyDepth - 1 - sourceRootDepth;
                            if (maximumRelativeDepth < 0)
                            {
                                warnings.Add($"Nested Prefab {referencedGuid} would exceed the {MaximumHierarchyDepth}-level combined hierarchy depth budget; this instance was left as a reference.");
                                continue;
                            }
                            TrimHierarchy(sourceParse.Roots, maximumRelativeDepth, warnings, referencedGuid);
                            activeGuids.Add(referencedGuid);
                            AddDirectRendererIdentities(sourceParse.Roots, referencedGuid);
                            ExpandInstances(sourceParse.Roots, referencedGuid, depth + 1, sourceRootDepth, activeGuids);
                            activeGuids.Remove(referencedGuid);
                            AddRendererAliases(sourceParse.Roots, containingGuid, referencedGuid, node.FileId);
                            ApplyMaterialOverrides(sourceParse.Roots, node.MaterialOverrides, warnings, cancellationToken);
                            MergeResolvedPrefab(node, sourceParse.Roots, warnings);
                        }
                    }
                    else if (!resolverFailures.Contains(referencedGuid))
                    {
                        warnings.Add($"Nested Prefab {referencedGuid} was not found or is not a text Prefab; its reference was retained.");
                    }
                }
            }
        }
    }

    private static int CountNodes(IEnumerable<PrefabNode> roots)
    {
        var count = 0;
        var pending = new Stack<PrefabNode>(roots);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            count++;
            foreach (var child in node.Children) pending.Push(child);
        }
        return count;
    }

    private static void TrimHierarchy(IReadOnlyList<PrefabNode> roots, int maximumRelativeDepth, ICollection<string> warnings, string sourceGuid)
    {
        var pending = new Stack<(PrefabNode Node, int Depth)>(roots.Select(x => (x, 0)));
        var trimmed = false;
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (depth >= maximumRelativeDepth && node.Children.Count > 0)
            {
                node.Children.Clear();
                trimmed = true;
                continue;
            }
            foreach (var child in node.Children) pending.Push((child, depth + 1));
        }
        if (trimmed)
            warnings.Add($"Nested Prefab {sourceGuid} was trimmed to keep the combined hierarchy within {MaximumHierarchyDepth} levels.");
    }

    private static void AddDirectRendererIdentities(IEnumerable<PrefabNode> roots, string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid)) return;
        var pending = new Stack<PrefabNode>(roots);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var componentId in node.RendererComponentFileIds)
                if (!node.RendererSourceIdentities.Any(x => x.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase) && x.FileId == componentId))
                    node.RendererSourceIdentities.Add(new PrefabSourceObjectIdentity(guid, componentId));
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    private static void AddRendererAliases(IEnumerable<PrefabNode> roots, string? containingGuid, string referencedGuid, long instanceFileId)
    {
        if (string.IsNullOrWhiteSpace(containingGuid)) return;
        var pending = new Stack<PrefabNode>(roots);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var sourceIdentity in node.RendererSourceIdentities.Where(x => x.Guid.Equals(referencedGuid, StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var aliasId = (sourceIdentity.FileId ^ instanceFileId) & long.MaxValue;
                if (!node.RendererSourceIdentities.Any(x => x.Guid.Equals(containingGuid, StringComparison.OrdinalIgnoreCase) && x.FileId == aliasId))
                    node.RendererSourceIdentities.Add(new PrefabSourceObjectIdentity(containingGuid, aliasId));
            }
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    internal static void ApplyMaterialOverrides(
        IEnumerable<PrefabNode> roots,
        IReadOnlyList<PrefabMaterialOverride> overrides,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var rendererIndex = new Dictionary<(string Guid, long FileId), HashSet<PrefabNode>>();
        var pending = new Stack<PrefabNode>(roots);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = pending.Pop();
            if (node.RendererComponentFileIds.Count == 1)
            {
                foreach (var identity in node.RendererSourceIdentities)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (identity.Guid.ToUpperInvariant(), identity.FileId);
                    if (!rendererIndex.TryGetValue(key, out var candidates))
                    {
                        candidates = new HashSet<PrefabNode>();
                        rendererIndex.Add(key, candidates);
                    }
                    candidates.Add(node);
                }
            }
            foreach (var child in node.Children) pending.Push(child);
        }

        foreach (var materialOverride in overrides)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (materialOverride.TargetGuid is null)
            {
                warnings.Add($"Material override for source object {materialOverride.TargetFileId} has no source GUID and was not applied.");
                continue;
            }
            if (!rendererIndex.TryGetValue((materialOverride.TargetGuid.ToUpperInvariant(), materialOverride.TargetFileId), out var matches)
                || matches.Count != 1)
            {
                warnings.Add(matches is null || matches.Count == 0
                    ? $"Material override target {materialOverride.TargetGuid}:{materialOverride.TargetFileId} could not be mapped safely and was not applied."
                    : $"Material override target {materialOverride.TargetGuid}:{materialOverride.TargetFileId} matched multiple renderers and was not applied.");
                continue;
            }
            var renderer = matches.Single();
            if (materialOverride.MaterialSlot < 0 || materialOverride.MaterialSlot >= renderer.Materials.Count)
            {
                warnings.Add($"Material override target {materialOverride.TargetGuid}:{materialOverride.TargetFileId} slot {materialOverride.MaterialSlot} is outside the source renderer's material list and was not applied.");
                continue;
            }
            renderer.Materials[materialOverride.MaterialSlot] = materialOverride.Material;
        }
    }

    private static void MergeResolvedPrefab(PrefabNode instance, IReadOnlyList<PrefabNode> sourceRoots, ICollection<string> warnings)
    {
        if (sourceRoots.Count == 0)
        {
            warnings.Add($"Nested Prefab {instance.SourcePrefab?.Guid} contains no hierarchy; its instance node was retained.");
            return;
        }
        instance.MaterialOverrides.Clear();
        if (sourceRoots.Count == 1)
        {
            var source = sourceRoots[0];
            instance.Name = source.Name;
            if (instance.HasRootTransformOverrides) ApplyTransformOverrides(source, instance.RootTransformOverrides);
            instance.Position = source.Position;
            instance.Rotation = source.Rotation;
            instance.Scale = source.Scale;
            instance.TransformFileId = source.TransformFileId;
            instance.Components.AddRange(source.Components);
            instance.Mesh ??= source.Mesh;
            instance.Materials.Clear();
            instance.Materials.AddRange(source.Materials);
            instance.RendererComponentFileIds.AddRange(source.RendererComponentFileIds);
            foreach (var identity in source.RendererSourceIdentities)
                if (!instance.RendererSourceIdentities.Contains(identity)) instance.RendererSourceIdentities.Add(identity);
            instance.Children.AddRange(source.Children);
            instance.SourcePrefab = source.SourcePrefab;
            if (source.MaterialOverrides.Count > 0) instance.MaterialOverrides.AddRange(source.MaterialOverrides);
        }
        else
        {
            if (instance.HasRootTransformOverrides)
            {
                var root = sourceRoots.FirstOrDefault(x => x.TransformFileId == instance.RootTransformTargetFileId);
                if (root is null) warnings.Add("Nested Prefab root transform override could not be matched to one source root; its transform remains approximate.");
                else ApplyTransformOverrides(root, instance.RootTransformOverrides);
                instance.Position = Vector3Value.Zero;
                instance.Rotation = QuaternionValue.Identity;
                instance.Scale = Vector3Value.One;
            }
            instance.Children.AddRange(sourceRoots);
        }
    }

    private static void ApplyTransformOverrides(PrefabNode node, IReadOnlyDictionary<string, string> overrides)
    {
        node.Position = new Vector3Value(
            ReadOverrideFloat(overrides, "m_LocalPosition.x", node.Position.X),
            ReadOverrideFloat(overrides, "m_LocalPosition.y", node.Position.Y),
            ReadOverrideFloat(overrides, "m_LocalPosition.z", node.Position.Z));
        node.Rotation = new QuaternionValue(
            ReadOverrideFloat(overrides, "m_LocalRotation.x", node.Rotation.X),
            ReadOverrideFloat(overrides, "m_LocalRotation.y", node.Rotation.Y),
            ReadOverrideFloat(overrides, "m_LocalRotation.z", node.Rotation.Z),
            ReadOverrideFloat(overrides, "m_LocalRotation.w", node.Rotation.W));
        node.Scale = new Vector3Value(
            ReadOverrideFloat(overrides, "m_LocalScale.x", node.Scale.X),
            ReadOverrideFloat(overrides, "m_LocalScale.y", node.Scale.Y),
            ReadOverrideFloat(overrides, "m_LocalScale.z", node.Scale.Z));
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

    public static IReadOnlyList<PrefabModelPlacement> GetModelPlacements(
        IEnumerable<PrefabNode> roots,
        bool useRootTransformForSkinnedMeshRenderers = false)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var references = new List<(PrefabNode Node, string Guid, Matrix4x4 Transform, long MeshFileId)>();
        foreach (var root in roots) CollectModelPlacements(root, Matrix4x4.Identity, null, useRootTransformForSkinnedMeshRenderers, references);

        return references
            .GroupBy(x => (Guid: x.Guid.ToLowerInvariant(), x.Transform))
            .Select(group =>
            {
                var first = group.First();
                var materials = group.SelectMany(x => x.Node.Materials).DistinctBy(x => (x.FileId, x.Guid)).ToArray();
                var meshFileIds = group.Select(x => x.MeshFileId).Where(x => x != 0).Distinct().ToArray();
                var rendererAssignments = group
                    .Where(x => x.MeshFileId != 0)
                    .Select(x => new PrefabRendererMaterialAssignment(x.MeshFileId, x.Node.Name, x.Node.Materials.ToArray()))
                    .ToArray();
                return new PrefabModelPlacement(first.Guid, first.Transform, first.Node, materials, meshFileIds)
                {
                    RendererAssignments = rendererAssignments,
                    UsesRootTransformApproximation = useRootTransformForSkinnedMeshRenderers
                        && group.Any(x => x.Node.Components.Contains("SkinnedMeshRenderer", StringComparer.Ordinal)),
                    MaterialOverrides = group.SelectMany(x => x.Node.MaterialOverrides)
                        .DistinctBy(x => (x.TargetFileId, x.TargetGuid, x.MaterialSlot, x.Material.FileId, x.Material.Guid))
                        .ToArray()
                };
            })
            .ToArray();
    }

    private static void CollectModelPlacements(
        PrefabNode node,
        Matrix4x4 parent,
        Matrix4x4? rootTransform,
        bool useRootTransformForSkinnedMeshRenderers,
        ICollection<(PrefabNode Node, string Guid, Matrix4x4 Transform, long MeshFileId)> placements)
    {
        var local = Matrix4x4.CreateScale(node.Scale.X, node.Scale.Y, node.Scale.Z)
            * Matrix4x4.CreateFromQuaternion(new Quaternion(node.Rotation.X, node.Rotation.Y, node.Rotation.Z, node.Rotation.W))
            * Matrix4x4.CreateTranslation(node.Position.X, node.Position.Y, node.Position.Z);
        var world = local * parent;
        rootTransform ??= world;
        var guid = node.Mesh?.Guid ?? node.SourcePrefab?.Guid;
        var isSkinnedMeshRenderer = node.Components.Contains("SkinnedMeshRenderer", StringComparer.Ordinal);
        var placementTransform = useRootTransformForSkinnedMeshRenderers && isSkinnedMeshRenderer ? rootTransform.Value : world;
        if (!string.IsNullOrWhiteSpace(guid)) placements.Add((node, guid, placementTransform, node.Mesh?.FileId ?? 0));
        foreach (var child in node.Children) CollectModelPlacements(child, world, rootTransform, useRootTransformForSkinnedMeshRenderers, placements);
    }

    private static void BreakParentCyclesAndExcessiveDepth(IReadOnlyDictionary<long, PrefabNode> nodes, ICollection<string> warnings)
    {
        foreach (var node in nodes.Values)
        {
            if (node.ParentFileId == 0 || nodes.ContainsKey(node.ParentFileId)) continue;
            warnings.Add($"Parent object {node.ParentFileId} was not found for '{node.Name}'.");
            node.ParentFileId = 0;
        }

        var completed = new HashSet<long>();
        foreach (var start in nodes.Values)
        {
            if (completed.Contains(start.FileId)) continue;
            var chain = new List<PrefabNode>();
            var indexById = new Dictionary<long, int>();
            var current = start;
            while (current.ParentFileId != 0 && nodes.TryGetValue(current.ParentFileId, out var parent))
            {
                if (indexById.TryGetValue(current.FileId, out var cycleStart))
                {
                    var cut = chain.Skip(cycleStart).MinBy(x => x.FileId)!;
                    cut.ParentFileId = 0;
                    warnings.Add($"Prefab hierarchy cycle detected; parent link for '{cut.Name}' was ignored.");
                    break;
                }

                indexById[current.FileId] = chain.Count;
                chain.Add(current);
                current = parent;
                if (completed.Contains(current.FileId)) break;
            }

            foreach (var node in chain) completed.Add(node.FileId);
        }

        var children = nodes.Values
            .Where(node => node.ParentFileId != 0)
            .GroupBy(node => node.ParentFileId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var pending = new Stack<(PrefabNode Node, int Depth)>(nodes.Values
            .Where(node => node.ParentFileId == 0)
            .Select(node => (node, 1)));
        var visited = new HashSet<long>();
        var reportedDepthCut = false;
        while (pending.TryPop(out var item))
        {
            if (!visited.Add(item.Node.FileId) || !children.TryGetValue(item.Node.FileId, out var descendants)) continue;
            foreach (var child in descendants)
            {
                if (item.Depth >= MaximumHierarchyDepth)
                {
                    child.ParentFileId = 0;
                    if (!reportedDepthCut)
                    {
                        warnings.Add($"Prefab hierarchy exceeded {MaximumHierarchyDepth} levels; parent link for '{child.Name}' was ignored.");
                        reportedDepthCut = true;
                    }
                    pending.Push((child, 1));
                }
                else
                {
                    pending.Push((child, item.Depth + 1));
                }
            }
        }
    }

    public static long? FindRootTransformFileId(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var documents = SplitDocuments(yaml);
        var rootGameObjects = documents.Where(x => x.ClassId == 1).Select(x => x.FileId).ToHashSet();
        foreach (var document in documents.Where(x => x.ClassId is 4 or 224))
        {
            var gameObject = ReadSingleReference(document.Body, "m_GameObject")?.FileId ?? 0;
            var parent = ReadSingleReference(document.Body, "m_Father")?.FileId ?? 0;
            if (rootGameObjects.Contains(gameObject) && parent == 0) return document.FileId;
        }
        return null;
    }

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
        var match = Regex.Match(body, $"(?ms)^\\s*{Regex.Escape(key)}:\\s*(?<ref>\\{{.*?\\}})");
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

    private static Dictionary<PrefabTransformTarget, Dictionary<string, string>> ReadPrefabTransformOverrides(string body)
    {
        var values = new Dictionary<PrefabTransformTarget, Dictionary<string, string>>();
        var modifications = Regex.Matches(body, "(?ms)^\\s*-\\s*target:\\s*\\{(?<target>.*?)\\}(?<block>.*?)(?=^\\s*-\\s*target:|\\z)");
        foreach (Match modification in modifications)
        {
            var target = Regex.Match(modification.Groups["target"].Value, "fileID:\\s*(?<id>-?\\d+)(?:\\s*,\\s*guid:\\s*(?<guid>[0-9a-fA-F]{32}))?");
            var propertyPath = Regex.Match(modification.Groups["block"].Value, "(?m)^\\s*propertyPath:\\s*(?<value>[^\\r\\n]+)");
            var value = Regex.Match(modification.Groups["block"].Value, "(?m)^\\s*value:\\s*(?<value>[^\\r\\n]+)");
            if (!target.Success || !propertyPath.Success || !value.Success) continue;
            var targetKey = new PrefabTransformTarget(
                target.Groups["guid"].Success ? target.Groups["guid"].Value.ToLowerInvariant() : null,
                long.Parse(target.Groups["id"].Value, CultureInfo.InvariantCulture));
            if (!values.TryGetValue(targetKey, out var targetValues))
            {
                targetValues = new Dictionary<string, string>(StringComparer.Ordinal);
                values.Add(targetKey, targetValues);
            }
            targetValues[propertyPath.Groups["value"].Value.Trim()] = value.Groups["value"].Value.Trim();
        }
        return values;
    }

    private static IReadOnlyList<PrefabMaterialOverride> ReadPrefabMaterialOverrides(string body)
    {
        var materials = new List<PrefabMaterialOverride>();
        var modifications = Regex.Matches(body, "(?ms)^\\s*-\\s*target:\\s*\\{(?<target>.*?)\\}(?<block>.*?)(?=^\\s*-\\s*target:|\\z)");
        foreach (Match modification in modifications)
        {
            var propertyPath = Regex.Match(modification.Groups["block"].Value, "(?m)^\\s*propertyPath:\\s*m_Materials\\.Array\\.data\\[(?<slot>\\d+)\\]");
            if (!propertyPath.Success) continue;
            var target = Regex.Match(modification.Groups["target"].Value, "fileID:\\s*(?<id>-?\\d+)(?:\\s*,\\s*guid:\\s*(?<guid>[0-9a-fA-F]{32}))?");
            if (!target.Success) continue;
            var reference = ReadSingleReference(modification.Groups["block"].Value, "objectReference");
            if (reference is { Guid: not null, FileId: not 0 })
                materials.Add(new PrefabMaterialOverride(
                    long.Parse(target.Groups["id"].Value, CultureInfo.InvariantCulture),
                    target.Groups["guid"].Success ? target.Groups["guid"].Value.ToLowerInvariant() : null,
                    int.Parse(propertyPath.Groups["slot"].Value, CultureInfo.InvariantCulture),
                    reference));
        }
        return materials.DistinctBy(x => (x.TargetFileId, x.TargetGuid, x.MaterialSlot, x.Material.FileId, x.Material.Guid)).ToArray();
    }

    private static IReadOnlyDictionary<string, string> SelectRootTransformOverrides(
        IReadOnlyDictionary<PrefabTransformTarget, Dictionary<string, string>> overrides,
        string? sourceGuid,
        long? rootTransformFileId,
        ICollection<string> warnings)
    {
        if (rootTransformFileId is not null)
            return sourceGuid is not null && overrides.TryGetValue(new PrefabTransformTarget(sourceGuid.ToLowerInvariant(), rootTransformFileId.Value), out var rootOverrides)
                ? rootOverrides
                : new Dictionary<string, string>(StringComparer.Ordinal);

        var transformOverrides = overrides.Where(x =>
            (sourceGuid is null || string.Equals(x.Key.Guid, sourceGuid, StringComparison.OrdinalIgnoreCase))
            && x.Value.Keys.Any(IsTransformOverride)).ToArray();
        if (transformOverrides.Length == 1) return transformOverrides[0].Value;
        if (transformOverrides.Length > 1)
            warnings.Add("Prefab instance transform overrides target multiple objects; the root transform could not be identified.");
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static bool IsTransformOverride(string path) =>
        path.StartsWith("m_LocalPosition.", StringComparison.Ordinal)
        || path.StartsWith("m_LocalRotation.", StringComparison.Ordinal)
        || path.StartsWith("m_LocalScale.", StringComparison.Ordinal);

    private static Vector3Value ReadOverrideVector(IReadOnlyDictionary<string, string> values, string prefix, Vector3Value fallback) =>
        new(ReadOverrideFloat(values, $"{prefix}.x", fallback.X),
            ReadOverrideFloat(values, $"{prefix}.y", fallback.Y),
            ReadOverrideFloat(values, $"{prefix}.z", fallback.Z));

    private static QuaternionValue ReadOverrideQuaternion(IReadOnlyDictionary<string, string> values, QuaternionValue fallback) =>
        new(ReadOverrideFloat(values, "m_LocalRotation.x", fallback.X),
            ReadOverrideFloat(values, "m_LocalRotation.y", fallback.Y),
            ReadOverrideFloat(values, "m_LocalRotation.z", fallback.Z),
            ReadOverrideFloat(values, "m_LocalRotation.w", fallback.W));

    private static float ReadOverrideFloat(IReadOnlyDictionary<string, string> values, string path, float fallback)
    {
        return values.TryGetValue(path, out var text)
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
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
    private readonly record struct PrefabTransformTarget(string? Guid, long FileId);
    private sealed record PrefabInstanceData(
        long FileId,
        UnityAssetReference SourcePrefab,
        long ParentTransformFileId,
        IReadOnlyDictionary<string, string> TransformOverrides,
        IReadOnlyList<PrefabMaterialOverride> MaterialOverrides,
        long? RootTransformFileId);
}

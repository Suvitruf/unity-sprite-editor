using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.DataAccess
{
    // JsonUtility-friendly mirror of the model. Everything Unity's serializer cannot handle
    // directly is adapted here: GUIDs become strings, and shape paths (List<Vector2[]> —
    // nested arrays) are wrapped one level. Commands map to a fixed two-level structure
    // because composites only ever contain leaves, and JsonUtility cannot do recursion.

    [Serializable]
    public class ShapePathDto
    {
        public Vector2[] Points;
    }

    [Serializable]
    public class RegionDto
    {
        public string Id;
        public string Name;
        public Rect Rect;
        public int Alignment;
        public Vector2 Pivot;
        public Vector4 Border;
        public string CustomData;
        public ShapePathDto[] Outlines;
        public float OutlineTessellation;
        public ShapePathDto[] PhysicsOutlines;
        public float PhysicsTessellation;
    }

    [Serializable]
    public class LeafCommandDto
    {
        public int Kind; // 0 = Modify, 1 = Create, 2 = Delete, 3 = Reorder
        public int FromIndex;
        public int ToIndex;
        public string Label;
        public string RegionId;
        public RegionDto Before;
        public RegionDto After;
        public RegionDto Created;
        public RegionDto[] Deleted;
        public int[] DeletedIndices;
    }

    [Serializable]
    public class CommandDto
    {
        public bool IsComposite;
        public string Label;
        public LeafCommandDto Leaf;
        public LeafCommandDto[] Children;
    }

    [Serializable]
    public class HistoryDto
    {
        public CommandDto[] Undo;
        public CommandDto[] Redo;
        public int SavePointDepth;
    }

    [Serializable]
    public class DocumentSnapshotDto
    {
        public int Version;
        public string BaselineRevision;
        public string TextureGuid;
        public string AssetPath;
        public string MetaHash;
        public long SavedAtTicks;
        public int TextureWidth;
        public int TextureHeight;
        public float PixelsPerUnit;
        public int ImportMode;
        public RegionDto[] Regions;
        public HistoryDto History;
        public string[] SelectedIds;
    }

    /// <summary>
    /// Serializes the working document, history and selection so unapplied edits survive
    /// domain reloads and crashes. The meta hash detects the one case where restoring would
    /// be wrong: the asset changed underneath the snapshot.
    /// </summary>
    public static class DocumentSnapshot
    {
        /// <summary>Older history beyond this many entries is sacrificed to keep files small.</summary>
        private const int maxHistoryEntries = 100;

        public static DocumentSnapshotDto Capture(
            SpriteDocument document, HistoryStack history, SelectionModel selection)
        {
            var regions = new RegionDto[document.Regions.Count];
            for (int i = 0; i < regions.Length; i++)
                regions[i] = ToDto(document.Regions[i]);

            var selectedIds = new List<string>();
            foreach (var id in selection.OrderedIds)
                selectedIds.Add(id.ToString());

            return new DocumentSnapshotDto
            {
                Version = 1,
                BaselineRevision = document.BaselineRevision,
                TextureGuid = document.AssetGuid,
                AssetPath = document.AssetPath,
                MetaHash = string.Empty, // retained only for recognizing legacy snapshots
                SavedAtTicks = System.DateTime.UtcNow.Ticks,
                TextureWidth = document.TextureWidth,
                TextureHeight = document.TextureHeight,
                PixelsPerUnit = document.PixelsPerUnit,
                ImportMode = (int)document.ImportMode,
                Regions = regions,
                History = CaptureHistory(history),
                SelectedIds = selectedIds.ToArray()
            };
        }

        public static SpriteDocument RestoreDocument(DocumentSnapshotDto dto, Texture2D texture)
        {
            Validate(dto);
            var document = new SpriteDocument(
                AssetDatabase.GUIDToAssetPath(dto.TextureGuid), texture, dto.TextureWidth, dto.TextureHeight,
                dto.PixelsPerUnit, (SpriteImportMode)dto.ImportMode);
            document.AssetGuid = dto.TextureGuid;
            document.BaselineRevision = dto.BaselineRevision;
            foreach (var regionDto in dto.Regions)
                document.Regions.Add(FromDto(regionDto));
            return document;
        }

        public static void RestoreHistory(HistoryDto dto, SpriteDocument document, HistoryStack history)
        {
            if (dto == null)
                throw new InvalidDataException("Recovery history is missing.");
            var instances = new Dictionary<GUID, SpriteRegion>();
            foreach (var region in document.Regions)
                instances.Add(region.Id, region);
            var undo = FromDtos(dto.Undo, document, instances);
            var redo = FromDtos(dto.Redo, document, instances);
            history.RestoreState(undo, redo, dto.SavePointDepth);
        }

        internal static void Validate(DocumentSnapshotDto dto)
        {
            if (dto == null || dto.Version != 1 || string.IsNullOrEmpty(dto.BaselineRevision)
                || dto.TextureWidth <= 0 || dto.TextureHeight <= 0 || dto.Regions == null
                || dto.SelectedIds == null || dto.History?.Undo == null || dto.History.Redo == null)
                throw new InvalidDataException("Unsupported or incomplete recovery file.");
            ValidateId(dto.TextureGuid);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var region in dto.Regions)
            {
                ValidateRegion(region);
                if (!ids.Add(region.Id))
                    throw new InvalidDataException("Duplicate sprite identity in recovery.");
            }
            foreach (var id in dto.SelectedIds)
                ValidateId(id);
            if (dto.History.SavePointDepth < -1
                || dto.History.SavePointDepth > dto.History.Undo.Length + dto.History.Redo.Length)
                throw new InvalidDataException("Invalid recovery save point.");
            foreach (var command in dto.History.Undo)
                ValidateCommand(command);
            foreach (var command in dto.History.Redo)
                ValidateCommand(command);
        }

        private static void ValidateId(string id)
        {
            if (id == null || id.Length != 32)
                throw new InvalidDataException("Invalid recovery GUID.");
            foreach (char c in id)
                if (!Uri.IsHexDigit(c))
                    throw new InvalidDataException("Invalid recovery GUID.");
        }

        private static void ValidateRegion(RegionDto region)
        {
            if (region == null || string.IsNullOrWhiteSpace(region.Name)
                || !Finite(region.Rect.x) || !Finite(region.Rect.y)
                || !Finite(region.Rect.width) || !Finite(region.Rect.height)
                || region.Rect.width <= 0 || region.Rect.height <= 0
                || !Finite(region.Pivot.x) || !Finite(region.Pivot.y))
                throw new InvalidDataException("Invalid recovered sprite.");
            ValidateId(region.Id);
            ValidatePaths(region.Outlines);
            ValidatePaths(region.PhysicsOutlines);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static void ValidatePaths(ShapePathDto[] paths)
        {
            foreach (var path in paths ?? Array.Empty<ShapePathDto>())
            {
                if (path?.Points == null)
                    throw new InvalidDataException("Invalid recovery path.");
                foreach (var point in path.Points)
                    if (!Finite(point.x) || !Finite(point.y))
                        throw new InvalidDataException("Invalid recovery point.");
            }
        }

        private static void ValidateCommand(CommandDto command)
        {
            if (command == null)
                throw new InvalidDataException("Missing recovery command.");
            if (!command.IsComposite)
                ValidateLeaf(command.Leaf);
            else
            {
                if (command.Children == null || command.Children.Length == 0)
                    throw new InvalidDataException("Empty recovery composite.");
                foreach (var child in command.Children)
                    ValidateLeaf(child);
            }
        }

        private static void ValidateLeaf(LeafCommandDto leaf)
        {
            if (leaf == null)
                throw new InvalidDataException("Missing recovery command.");
            switch (leaf.Kind)
            {
                case 0:
                    ValidateId(leaf.RegionId);
                    ValidateRegion(leaf.Before);
                    ValidateRegion(leaf.After);
                    if (leaf.Before.Id != leaf.RegionId || leaf.After.Id != leaf.RegionId)
                        throw new InvalidDataException("Mismatched recovery identity.");
                    break;
                case 1:
                    ValidateRegion(leaf.Created);
                    break;
                case 2:
                    if (leaf.Deleted == null || leaf.DeletedIndices == null
                        || leaf.Deleted.Length != leaf.DeletedIndices.Length)
                        throw new InvalidDataException("Invalid recovered deletion.");
                    for (int i = 0; i < leaf.Deleted.Length; i++)
                    {
                        ValidateRegion(leaf.Deleted[i]);
                        if (leaf.DeletedIndices[i] < 0)
                            throw new InvalidDataException("Invalid deletion index.");
                    }
                    break;
                case 3:
                    ValidateId(leaf.RegionId);
                    if (leaf.FromIndex < 0 || leaf.ToIndex < 0)
                        throw new InvalidDataException("Invalid reorder index.");
                    break;
                default:
                    throw new InvalidDataException("Unsupported recovery command.");
            }
        }

        public static string ComputeMetaHash(string assetPath)
        {
            string metaPath = assetPath + ".meta";
            if (!File.Exists(metaPath))
                return string.Empty;
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(File.ReadAllText(metaPath)));
            return Convert.ToBase64String(hash);
        }

        // --- History ---

        private static HistoryDto CaptureHistory(HistoryStack history)
        {
            var undoEntries = history.UndoEntries;
            int skip = Mathf.Max(0, undoEntries.Count - maxHistoryEntries);
            int savePoint = history.SavePointDepth;
            // Truncation shifts positions; a save point inside the dropped span is gone.
            savePoint = savePoint < skip ? -1 : savePoint - skip;

            var undo = new List<CommandDto>();
            for (int i = skip; i < undoEntries.Count; i++)
            {
                var dto = ToDto(undoEntries[i]);
                if (dto != null)
                    undo.Add(dto);
            }
            var redo = new List<CommandDto>();
            var redoEntries = history.RedoEntries;
            int redoStart = Mathf.Max(0, redoEntries.Count - maxHistoryEntries);
            for (int i = redoStart; i < redoEntries.Count; i++)
                redo.Add(ToDto(redoEntries[i]));
            if (savePoint > undo.Count + redo.Count)
                savePoint = -1;

            return new HistoryDto
            {
                Undo = undo.ToArray(),
                Redo = redo.ToArray(),
                SavePointDepth = savePoint
            };
        }

        private static List<IEditorCommand> FromDtos(CommandDto[] dtos, SpriteDocument document, Dictionary<GUID, SpriteRegion> instances)
        {
            var commands = new List<IEditorCommand>();
            if (dtos == null)
                return commands;
            foreach (var dto in dtos)
            {
                var command = FromDto(dto, document, instances);
                if (command != null)
                    commands.Add(command);
            }
            return commands;
        }

        private static CommandDto ToDto(IEditorCommand command)
        {
            if (command is CompositeCommand composite)
            {
                var children = new List<LeafCommandDto>();
                foreach (var child in composite.Children)
                {
                    var leaf = ToLeafDto(child);
                    if (leaf != null)
                        children.Add(leaf);
                }
                return new CommandDto
                {
                    IsComposite = true, Label = composite.Label, Children = children.ToArray()
                };
            }

            var single = ToLeafDto(command);
            return single == null ? null : new CommandDto { Leaf = single, Label = command.Label };
        }

        private static LeafCommandDto ToLeafDto(IEditorCommand command)
        {
            switch (command)
            {
                case ModifyRegionCommand modify:
                    return new LeafCommandDto
                    {
                        Kind = 0,
                        Label = modify.Label,
                        RegionId = modify.RegionId.ToString(),
                        Before = ToDto(modify.Before),
                        After = ToDto(modify.After)
                    };
                case CreateRegionCommand create:
                    return new LeafCommandDto { Kind = 1, Created = ToDto(create.Region) };
                case DeleteRegionsCommand delete:
                {
                    var regions = new RegionDto[delete.Entries.Count];
                    var indices = new int[delete.Entries.Count];
                    for (int i = 0; i < delete.Entries.Count; i++)
                    {
                        regions[i] = ToDto(delete.Entries[i].region);
                        indices[i] = delete.Entries[i].index;
                    }
                    return new LeafCommandDto
                    {
                        Kind = 2, Label = delete.Label, Deleted = regions, DeletedIndices = indices
                    };
                }
                case ReorderRegionCommand reorder:
                    return new LeafCommandDto { Kind = 3, RegionId = reorder.RegionId.ToString(),
                        FromIndex = reorder.FromIndex, ToIndex = reorder.ToIndex, Label = reorder.Label };
                default:
                    throw new InvalidDataException($"Unsupported history command: {command.GetType().Name}");
            }
        }

        private static IEditorCommand FromDto(CommandDto dto, SpriteDocument document, Dictionary<GUID, SpriteRegion> instances)
        {
            if (dto == null)
                return null;
            if (!dto.IsComposite)
                return FromLeafDto(dto.Leaf, document, instances);

            var children = new List<IEditorCommand>();
            foreach (var childDto in dto.Children)
            {
                var child = FromLeafDto(childDto, document, instances);
                if (child != null)
                    children.Add(child);
            }
            return children.Count == 0 ? null : new CompositeCommand(dto.Label, children);
        }

        private static IEditorCommand FromLeafDto(LeafCommandDto dto, SpriteDocument document, Dictionary<GUID, SpriteRegion> instances)
        {
            switch (dto?.Kind)
            {
                case 0:
                    return new ModifyRegionCommand(document, new GUID(dto.RegionId),
                        FromDto(dto.Before), FromDto(dto.After), dto.Label);
                case 1:
                    // Identity matters: if this create is applied (in the undo stack), its
                    // region must BE the document's instance, or Undo's Remove-by-reference
                    // would miss. Resolve first, construct only when absent.
                    return new CreateRegionCommand(document, ResolveRegion(dto.Created, instances));
                case 2:
                {
                    var entries = new List<(SpriteRegion region, int index)>();
                    for (int i = 0; i < dto.Deleted.Length; i++)
                        entries.Add((ResolveRegion(dto.Deleted[i], instances), dto.DeletedIndices[i]));
                    return new DeleteRegionsCommand(document, entries, dto.Label);
                }
                case 3:
                    return new ReorderRegionCommand(document, new GUID(dto.RegionId), dto.FromIndex, dto.ToIndex);
                default:
                    throw new InvalidDataException("Unsupported recovery command.");
            }
        }

        private static SpriteRegion ResolveRegion(RegionDto dto, Dictionary<GUID, SpriteRegion> instances)
        {
            var id = new GUID(dto.Id);
            if (!instances.TryGetValue(id, out var region))
            {
                region = FromDto(dto);
                instances.Add(id, region);
            }
            return region;
        }

        // --- Regions ---

        private static RegionDto ToDto(SpriteRegion region)
        {
            return new RegionDto
            {
                Id = region.Id.ToString(),
                Name = region.Name,
                Rect = region.Rect,
                Alignment = (int)region.Alignment,
                Pivot = region.Pivot,
                Border = region.Border,
                CustomData = region.CustomData,
                Outlines = ToDto(region.Outlines),
                OutlineTessellation = region.OutlineTessellation,
                PhysicsOutlines = ToDto(region.PhysicsOutlines),
                PhysicsTessellation = region.PhysicsTessellation
            };
        }

        private static SpriteRegion FromDto(RegionDto dto)
        {
            return new SpriteRegion
            {
                Id = new GUID(dto.Id),
                Name = dto.Name,
                Rect = dto.Rect,
                Alignment = (SpriteAlignment)dto.Alignment,
                Pivot = dto.Pivot,
                Border = dto.Border,
                CustomData = dto.CustomData,
                Outlines = FromDto(dto.Outlines),
                OutlineTessellation = dto.OutlineTessellation,
                PhysicsOutlines = FromDto(dto.PhysicsOutlines),
                PhysicsTessellation = dto.PhysicsTessellation
            };
        }

        private static ShapePathDto[] ToDto(List<Vector2[]> paths)
        {
            if (paths == null)
                return Array.Empty<ShapePathDto>();
            var result = new ShapePathDto[paths.Count];
            for (int i = 0; i < paths.Count; i++)
                result[i] = new ShapePathDto { Points = paths[i] ?? Array.Empty<Vector2>() };
            return result;
        }

        private static List<Vector2[]> FromDto(ShapePathDto[] paths)
        {
            var result = new List<Vector2[]>(paths?.Length ?? 0);
            if (paths == null)
                return result;
            foreach (var path in paths)
                result.Add(path?.Points ?? Array.Empty<Vector2>());
            return result;
        }
    }

    /// <summary>
    /// Recovery files under Library/ — per project, out of version control, and they outlive
    /// a crashed editor process, which is the point.
    /// </summary>
    public static class RecoveryStore
    {
        private const string directory = "Library/BetterSpriteEditor";

        private static string PathFor(string textureGuid)
        {
            return $"{directory}/recovery-{textureGuid}.json";
        }

        public static void Save(DocumentSnapshotDto dto)
        {
            try
            {
                AssetState.WriteAtomic(PathFor(dto.TextureGuid), JsonUtility.ToJson(dto));
            }
            catch (IOException exception)
            {
                Debug.LogWarning($"[BetterSpriteEditor] Could not write recovery file: {exception.Message}");
            }
        }

        public static DocumentSnapshotDto TryLoad(string textureGuid)
        {
            try
            {
                string path = PathFor(textureGuid);
                if (!File.Exists(path))
                    return null;
                var dto = JsonUtility.FromJson<DocumentSnapshotDto>(File.ReadAllText(path));
                if (string.IsNullOrEmpty(dto?.TextureGuid))
                    throw new InvalidDataException("Recovery texture identity is missing.");
                return dto;
            }
            catch (Exception exception)
            {
                Quarantine(textureGuid, exception.Message);
                return null;
            }
        }

        internal static void Quarantine(string textureGuid, string reason)
        {
            try
            {
                string path = PathFor(textureGuid);
                if (!File.Exists(path))
                    return;
                string retained = path + ".retained-" + DateTime.UtcNow.Ticks;
                File.Move(path, retained);
                Debug.LogWarning($"[BetterSpriteEditor] Recovery retained at '{retained}': {reason}");
            }
            catch (IOException exception)
            {
                Debug.LogWarning($"[BetterSpriteEditor] Could not archive recovery: {exception.Message}");
            }
        }

        public static void Delete(string textureGuid)
        {
            try
            {
                string path = PathFor(textureGuid);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Leftover file is harmless; it will be overwritten or ignored.
            }
        }
    }
}

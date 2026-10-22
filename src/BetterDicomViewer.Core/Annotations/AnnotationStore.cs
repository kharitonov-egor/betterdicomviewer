using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterDicomViewer.Core.Annotations;

[JsonSerializable(typeof(List<Annotation>))]
[JsonSourceGenerationOptions(WriteIndented = true, Converters = [typeof(JsonStringEnumConverter<AnnotationKind>)])]
internal sealed partial class AnnotationJsonContext : JsonSerializerContext;

/// <summary>Annotations grouped per study and persisted under the local app-data folder (never next to the scans).</summary>
public sealed class AnnotationStore
{
    private readonly Dictionary<string, List<Annotation>> _byStudy = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public event Action<string>? Changed;

    public static string PathFor(string studyUid)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(studyUid)))[..24];
        return Path.Combine(AppPaths.AnnotationsDir, hash + ".json");
    }

    public IReadOnlyList<Annotation> ForStudy(string studyUid)
    {
        lock (_gate)
        {
            if (!_byStudy.TryGetValue(studyUid, out var list))
            {
                list = Load(studyUid);
                _byStudy[studyUid] = list;
            }
            return list.ToList();
        }
    }

    public IReadOnlyList<Annotation> All()
    {
        lock (_gate) return _byStudy.Values.SelectMany(v => v).ToList();
    }

    public void Add(Annotation a)
    {
        lock (_gate)
        {
            EnsureLoaded(a.StudyInstanceUid).Add(a);
        }
        Persist(a.StudyInstanceUid);
    }

    public void Remove(Annotation a)
    {
        lock (_gate)
        {
            EnsureLoaded(a.StudyInstanceUid).RemoveAll(x => x.Id == a.Id);
        }
        Persist(a.StudyInstanceUid);
    }

    public void Replace(Annotation updated)
    {
        lock (_gate)
        {
            var list = EnsureLoaded(updated.StudyInstanceUid);
            var i = list.FindIndex(x => x.Id == updated.Id);
            if (i >= 0) list[i] = updated;
            else list.Add(updated);
        }
        Persist(updated.StudyInstanceUid);
    }

    public string NextLabel(string studyUid, AnnotationKind kind)
    {
        var prefix = kind switch
        {
            AnnotationKind.Length => "L",
            AnnotationKind.Angle => "A",
            AnnotationKind.Ellipse or AnnotationKind.Rectangle or AnnotationKind.Freehand => "R",
            AnnotationKind.Probe => "P",
            AnnotationKind.Arrow => "N",
            _ => "T",
        };
        lock (_gate)
        {
            var list = EnsureLoaded(studyUid);
            var n = 1;
            while (list.Any(x => x.Label == prefix + n)) n++;
            return prefix + n;
        }
    }

    private List<Annotation> EnsureLoaded(string studyUid)
    {
        if (!_byStudy.TryGetValue(studyUid, out var list))
        {
            list = Load(studyUid);
            _byStudy[studyUid] = list;
        }
        return list;
    }

    private static List<Annotation> Load(string studyUid)
    {
        var path = PathFor(studyUid);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), AnnotationJsonContext.Default.ListAnnotation) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void Persist(string studyUid)
    {
        List<Annotation> snapshot;
        lock (_gate) snapshot = EnsureLoaded(studyUid).ToList();
        try
        {
            var path = PathFor(studyUid);
            if (snapshot.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, JsonSerializer.Serialize(snapshot, AnnotationJsonContext.Default.ListAnnotation));
            }
        }
        catch (IOException)
        {
        }
        Changed?.Invoke(studyUid);
    }
}

public interface IUndoableAction
{
    string Description { get; }
    void Undo();
    void Redo();
}

public sealed class DelegateAction(string description, Action undo, Action redo) : IUndoableAction
{
    public string Description { get; } = description;
    public void Undo() => undo();
    public void Redo() => redo();
}

/// <summary>Linear undo history shared by measurements and segmentation edits. Actions are pushed after they are applied.</summary>
public sealed class UndoStack(int capacity = 200)
{
    private readonly LinkedList<IUndoableAction> _undo = new();
    private readonly Stack<IUndoableAction> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Last?.Value.Description;
    public string? RedoDescription => _redo.TryPeek(out var a) ? a.Description : null;

    public void Push(IUndoableAction action)
    {
        _undo.AddLast(action);
        while (_undo.Count > capacity) _undo.RemoveFirst();
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_undo.Last is not { } node) return;
        _undo.RemoveLast();
        node.Value.Undo();
        _redo.Push(node.Value);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var action)) return;
        action.Redo();
        _undo.AddLast(action);
        Changed?.Invoke();
    }
}

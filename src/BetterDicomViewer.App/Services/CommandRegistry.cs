using System.Globalization;
using System.Windows.Input;

namespace BetterDicomViewer.App.Services;

public readonly record struct Shortcut(Key Key, ModifierKeys Modifiers)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(CultureInfo.InvariantCulture),
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemQuestion => "?",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Prior => "Page Up",
        Key.Next => "Page Down",
        Key.Escape => "Esc",
        Key.Delete => "Delete",
        Key.Space => "Space",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        _ => k.ToString(),
    };
}

/// <summary>A user-facing command. Its shortcuts drive key handling, tooltips, the shortcut reference and the command palette.</summary>
public sealed class AppCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public AppCommand(string id, string title, string category, Action execute, Func<bool>? canExecute, string? description, params Shortcut[] shortcuts)
    {
        Id = id;
        Title = title;
        Category = category;
        Description = description ?? "";
        _execute = execute;
        _canExecute = canExecute;
        Shortcuts = shortcuts;
    }

    public string Id { get; }
    public string Title { get; }
    public string Category { get; }
    public string Description { get; }
    public IReadOnlyList<Shortcut> Shortcuts { get; }
    public string ShortcutText => string.Join(", ", Shortcuts.Select(s => s.ToString()));
    public string ToolTip => Shortcuts.Count == 0 ? Title : $"{Title} ({ShortcutText})";

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter = null) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter = null)
    {
        if (CanExecute()) _execute();
    }
}

/// <summary>Documents a mouse or other non-command interaction so it appears in the shortcut reference.</summary>
public sealed record InteractionHint(string Category, string Gesture, string Action);

public sealed class CommandRegistry
{
    private readonly List<AppCommand> _commands = [];
    private readonly Dictionary<Shortcut, AppCommand> _byShortcut = [];

    public IReadOnlyList<AppCommand> All => _commands;
    public List<InteractionHint> Hints { get; } = [];

    public AppCommand Add(string id, string title, string category, Action execute, params Shortcut[] shortcuts) =>
        Add(id, title, category, execute, null, null, shortcuts);

    public AppCommand Add(string id, string title, string category, Action execute, Func<bool>? canExecute, string? description, params Shortcut[] shortcuts)
    {
        var cmd = new AppCommand(id, title, category, execute, canExecute, description, shortcuts);
        _commands.Add(cmd);
        foreach (var s in shortcuts)
        {
            if (_byShortcut.ContainsKey(s)) throw new InvalidOperationException($"Shortcut {s} is assigned twice.");
            _byShortcut[s] = cmd;
        }
        return cmd;
    }

    public AppCommand Get(string id) => _commands.First(c => c.Id == id);

    public bool TryHandle(Key key, ModifierKeys modifiers)
    {
        if (_byShortcut.TryGetValue(new Shortcut(key, modifiers), out var cmd) && cmd.CanExecute())
        {
            cmd.Execute();
            return true;
        }
        return false;
    }

    public static Shortcut K(Key key, ModifierKeys mods = ModifierKeys.None) => new(key, mods);
}

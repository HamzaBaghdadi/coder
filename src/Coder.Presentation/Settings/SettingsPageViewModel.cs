using System.Collections.ObjectModel;
using Coder.Presentation.Editors;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.Settings;

/// <summary>One setting on the settings page: what it is, why you'd change it, and its current value.</summary>
public abstract class SettingItem(string category, string title, string description) : ObservableObject
{
    public string Category { get; } = category;

    public string Title { get; } = title;

    public string Description { get; } = description;

    /// <summary>Extra words the search matches (e.g. "dark" for the color theme).</summary>
    public string Keywords { get; init; } = string.Empty;

    /// <summary>Shown under the description, e.g. "Applies after a restart".</summary>
    public string? Note { get; init; }

    /// <summary>The value may have changed elsewhere (a command, a key): show it again.</summary>
    public abstract void Refresh();

    public bool Matches(string query) =>
        query.Length == 0 || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word =>
            Title.Contains(word, StringComparison.OrdinalIgnoreCase) || Description.Contains(word, StringComparison.OrdinalIgnoreCase)
            || Category.Contains(word, StringComparison.OrdinalIgnoreCase) || Keywords.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// <summary>On or off.</summary>
public sealed class ToggleSetting(string category, string title, string description, Func<bool> get, Action<bool> set)
    : SettingItem(category, title, description)
{
    public bool Value
    {
        get => get();
        set
        {
            if (value != get())
            {
                set(value);
                OnPropertyChanged();
            }
        }
    }

    public override void Refresh() => OnPropertyChanged(nameof(Value));
}

/// <summary>A number within limits.</summary>
public sealed class NumberSetting(string category, string title, string description, Func<double> get, Action<double> set, double minimum, double maximum)
    : SettingItem(category, title, description)
{
    public double Minimum { get; } = minimum;

    public double Maximum { get; } = maximum;

    public double Value
    {
        get => get();
        set
        {
            var clamped = Math.Clamp(Math.Round(value), Minimum, Maximum);
            if (clamped != get())
            {
                set(clamped);
            }

            OnPropertyChanged();
        }
    }

    public override void Refresh() => OnPropertyChanged(nameof(Value));
}

/// <summary>One of a few options.</summary>
public sealed class ChoiceSetting(string category, string title, string description, IReadOnlyList<SettingOption> options, Func<string> get, Action<string> set)
    : SettingItem(category, title, description)
{
    public IReadOnlyList<SettingOption> Options { get; } = options;

    public SettingOption? Selected
    {
        get => Options.FirstOrDefault(o => o.Id == get()) ?? (Options.Count > 0 ? Options[0] : null);
        set
        {
            if (value is not null && value.Id != get())
            {
                set(value.Id);
                OnPropertyChanged();
            }
        }
    }

    public override void Refresh() => OnPropertyChanged(nameof(Selected));
}

/// <summary>A button and a line saying the current state (Settings Sync: "Signed in as…", "Sync Now").</summary>
public sealed class ActionSetting(string category, string title, string description, Func<string> status, Func<string> actionLabel, Func<Task> run)
    : SettingItem(category, title, description)
{
    public string Status => status();

    public string ActionLabel => actionLabel();

    public Task RunAsync() => run();

    public override void Refresh()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(ActionLabel));
    }
}

public sealed record SettingOption(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The settings page (Ctrl+,), opened as a tab like in VS Code: every setting with a plain description,
/// grouped and searchable. Changes apply at once and are saved with the rest of the session.
/// </summary>
public sealed class SettingsPageViewModel : TabViewModel
{
    private string _query = string.Empty;

    public SettingsPageViewModel(IEnumerable<SettingItem> items, Func<Task>? openJson = null)
    {
        Items = items.ToList();
        OpenJson = openJson;
        Visible = new ObservableCollection<SettingItem>(Items);
    }

    public IReadOnlyList<SettingItem> Items { get; }

    /// <summary>The settings that match the search, in page order.</summary>
    public ObservableCollection<SettingItem> Visible { get; }

    /// <summary>"Open Settings (JSON)", for what the page doesn't show (language servers…).</summary>
    public Func<Task>? OpenJson { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                Visible.Clear();
                foreach (var item in Items.Where(i => i.Matches(_query.Trim())))
                {
                    Visible.Add(item);
                }

                OnPropertyChanged(nameof(HasResults));
            }
        }
    }

    public bool HasResults => Visible.Count > 0;

    public override string Title => "Settings";

    public override string Description => "Settings";

    public override string? FilePath
    {
        get => null;
        set { }
    }

    public void RefreshAll()
    {
        foreach (var item in Items)
        {
            item.Refresh();
        }
    }
}

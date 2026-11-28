using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using SuperBar.Core.Layout;
using SuperBar.Core.Mvvm;
using SuperBar.Core.Runtime;
using SuperBar.Core.Settings;
using SuperBar.Widgets;

namespace SuperBar.ControlCenter.ViewModels;

/// <summary>Creates the custom settings editor registered for a widget's settings type, if any (see <c>[WidgetSettingsViewFor]</c>).</summary>
public interface IWidgetSettingsEditors
{
    /// <summary>A UI element editing <paramref name="context"/>'s instance, or null to use the generated form.</summary>
    object? Create(WidgetSettingsEditContext context);
}

/// <summary>Collection edits shared by the pages that follow the settings live.</summary>
internal static class ObservableCollectionSync
{
    /// <summary>
    /// Brings <paramref name="items"/> to <paramref name="target"/> with the fewest collection edits: removes what
    /// left, then moves or inserts each entry into its slot. Items that stay put keep their containers (and focus).
    /// </summary>
    public static void SyncTo<T>(this ObservableCollection<T> items, IReadOnlyList<T> target)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(items[i]))
            {
                items.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            var current = items.IndexOf(target[i]);
            if (current == i)
            {
                continue;
            }

            if (current < 0)
            {
                items.Insert(i, target[i]);
            }
            else
            {
                items.Move(current, i);
            }
        }
    }
}

/// <summary>
/// The Widgets page: a grid of tiles for the widgets on the bar, an inspector with the selected widget's settings,
/// and the catalog of types that can be added. Follows the settings live and edits its collections in place, so the
/// selected tile, the inspector and keyboard focus survive every change (its own and those made on the bar).
/// </summary>
public sealed class WidgetsPageViewModel : PageViewModel, IDisposable
{
    // The bar read as one line.
    private static readonly TaskbarZone[] BarOrder = [TaskbarZone.Left, TaskbarZone.Center, TaskbarZone.Right];

    private readonly ISettingsService _settings;
    private readonly IWidgetRegistry _registry;
    private readonly IWidgetSettingsEditors _editors;
    private readonly ISettingsStore _store;
    private readonly IControlCenterUi _ui;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, WidgetItemViewModel> _items = new(StringComparer.Ordinal);
    private string? _signature;
    private WidgetItemViewModel? _selected;
    private bool _isEmpty = true;

    public WidgetsPageViewModel(
        ISettingsService settings,
        IWidgetRegistry registry,
        IWidgetSettingsEditors editors,
        ISettingsStore store,
        IControlCenterUi ui,
        IUiDispatcher dispatcher)
        : base("Widgets", "\uE71D", "Widgets live directly inside the bar. Add them, then select one to change its settings.")
    {
        _settings = settings;
        _registry = registry;
        _editors = editors;
        _store = store;
        _ui = ui;
        _dispatcher = dispatcher;
        Types = registry.All
            .OrderBy(d => d.DisplayName, StringComparer.CurrentCulture)
            .Select(d => new WidgetTypeViewModel(this, d))
            .ToArray();
        _settings.Changed += OnSettingsChanged;
        Reload();
    }

    /// <summary>The widgets on the bar, in the bar's left-to-right order: the tile grid. Edited in place, never cleared.</summary>
    public ObservableCollection<WidgetItemViewModel> Instances { get; } = new();

    /// <summary>
    /// The same items in an order that never changes (new ones are appended): one inspector layer each. Kept apart
    /// from <see cref="Instances"/> because an items control re-creates the container of a moved item, and the layer
    /// holds the controls the user is working with.
    /// </summary>
    public ObservableCollection<WidgetItemViewModel> Inspectors { get; } = new();

    /// <summary>Every widget type, by name: the "Add widget" catalog. Created once.</summary>
    public IReadOnlyList<WidgetTypeViewModel> Types { get; }

    /// <summary>No widget is on the bar.</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set
        {
            if (SetProperty(ref _isEmpty, value))
            {
                OnPropertyChanged(nameof(HasWidgets));
            }
        }
    }

    public bool HasWidgets => !_isEmpty;

    /// <summary>
    /// Raised after a widget was removed, with the tile that is selected now: the view moves keyboard focus there,
    /// because the button that had it is gone.
    /// </summary>
    internal event Action<WidgetItemViewModel>? TileFocusRequested;

    public override void OnNavigatedTo() => Reload();

    // Widgets are dragged on the bar and moved on the Layout page while this page is alive: follow live.
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _dispatcher.Invoke(Reload);

    internal void Select(WidgetItemViewModel? item)
    {
        if (ReferenceEquals(item, _selected))
        {
            return;
        }

        if (_selected is not null)
        {
            _selected.IsSelected = false;
        }

        _selected = item;
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    /// <summary>Adds an instance of the type in its default zone and selects it.</summary>
    internal void Add(WidgetDescriptor descriptor)
    {
        string? added = null;
        _settings.Update(s => added = WidgetInstances.Add(s, descriptor.TypeId, descriptor.DefaultZone, descriptor.AllowMultiple));
        Reload();
        if (added is not null && _items.TryGetValue(added, out var item))
        {
            Select(item);
        }
    }

    /// <summary>Asks first, then removes the widget from the bar together with its settings and state.</summary>
    internal async void Remove(WidgetItemViewModel item)
    {
        try
        {
            var confirmed = await _ui.ConfirmAsync(
                $"Remove {item.DisplayName} from the bar?",
                "The widget and its settings are deleted. You can add it again with Add widget.",
                "Remove",
                destructive: true);
            if (!confirmed)
            {
                return;
            }

            _settings.Update(s => WidgetInstances.Remove(s, item.InstanceId));
            try
            {
                // Its runtime state goes with it; a new instance starts fresh.
                _store.Delete(WidgetStateKeys.For(item.InstanceId));
            }
            catch (Exception)
            {
                // Only leaves an orphaned row behind.
            }

            Reload();
            if (_selected is { } next)
            {
                TileFocusRequested?.Invoke(next);
            }
        }
        catch (Exception)
        {
            // async void: nothing may escape. A dialog that fails to show removes nothing.
        }
    }

    /// <summary>Puts the widget at the end of <paramref name="zone"/>.</summary>
    internal void MoveToZone(WidgetItemViewModel item, TaskbarZone zone)
    {
        if (item.PlacementId is not { } placementId)
        {
            return;
        }

        _settings.Update(s => SectionOrdering.MoveToZone(s.Layout, placementId, zone));
        Reload();
    }

    /// <summary>
    /// Brings the page to the stored settings without clearing anything: item view models are reused by instance id,
    /// the collections are edited with Move / Insert / Remove only, and the values an item shows are refreshed in
    /// place (never through the writing setters).
    /// </summary>
    private void Reload()
    {
        var current = _settings.Current;
        var entries = InBarOrder(current);
        var signature = string.Join("|", entries.Select(e => string.Create(
            CultureInfo.InvariantCulture,
            $"{e.Instance.InstanceId}={e.Instance.TypeId}={e.Placement?.Id}={e.Placement?.Zone}={e.Instance.Enabled}={e.Instance.Width}={e.Instance.Options.ToJsonString()}")));
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        var selectedIndex = _selected is null ? 0 : Instances.IndexOf(_selected);

        var target = new List<WidgetItemViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            var id = entry.Instance.InstanceId;
            if (!_items.TryGetValue(id, out var item) || !ReferenceEquals(item.Descriptor, entry.Descriptor))
            {
                item = new WidgetItemViewModel(this, _settings, _editors, _dispatcher, entry.Instance, entry.Descriptor);
                _items[id] = item;
            }

            item.Sync(entry.Instance, entry.Placement);
            target.Add(item);
        }

        foreach (var id in _items.Keys.Where(id => !target.Contains(_items[id])).ToList())
        {
            _items.Remove(id);
        }

        Instances.SyncTo(target);

        // Inspector layers: drop what left, append what is new, never move.
        for (var i = Inspectors.Count - 1; i >= 0; i--)
        {
            if (!target.Contains(Inspectors[i]))
            {
                Inspectors.RemoveAt(i);
            }
        }

        foreach (var item in target)
        {
            if (!Inspectors.Contains(item))
            {
                Inspectors.Add(item);
            }
        }

        // A widget is always selected while there is one: after a removal, the tile that took its place.
        if (_selected is null || !target.Contains(_selected))
        {
            Select(target.Count == 0 ? null : target[Math.Clamp(selectedIndex, 0, target.Count - 1)]);
        }

        IsEmpty = target.Count == 0;
        foreach (var type in Types)
        {
            type.CanAdd = type.Descriptor.AllowMultiple || !current.Widgets.Any(w => w.TypeId == type.Descriptor.TypeId);
        }
    }

    /// <summary>
    /// The instances of known widget types in the order they appear on the bar (Left zone, then Center, then Right),
    /// followed by any that have no place in the layout.
    /// </summary>
    private List<Entry> InBarOrder(SuperBarSettings current)
    {
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var zone in BarOrder)
        {
            foreach (var placement in current.Layout.Where(p => p.Zone == zone && p.Kind == SectionKind.Widget))
            {
                if (current.Widgets.FirstOrDefault(w => w.InstanceId == placement.WidgetInstanceId) is { } instance
                    && seen.Add(instance.InstanceId)
                    && _registry.TryGet(instance.TypeId, out var descriptor))
                {
                    entries.Add(new Entry(instance, descriptor, placement));
                }
            }
        }

        foreach (var instance in current.Widgets)
        {
            if (seen.Add(instance.InstanceId) && _registry.TryGet(instance.TypeId, out var descriptor))
            {
                entries.Add(new Entry(instance, descriptor, null));
            }
        }

        return entries;
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private readonly record struct Entry(WidgetInstanceSettings Instance, WidgetDescriptor Descriptor, SectionPlacement? Placement);
}

/// <summary>A widget type in the "Add widget" catalog.</summary>
public sealed class WidgetTypeViewModel : ObservableObject
{
    private bool _canAdd = true;

    public WidgetTypeViewModel(WidgetsPageViewModel owner, WidgetDescriptor descriptor)
    {
        Descriptor = descriptor;
        AddCommand = new RelayCommand(() =>
        {
            if (CanAdd)
            {
                owner.Add(descriptor);
            }
        });
    }

    public WidgetDescriptor Descriptor { get; }
    public string DisplayName => Descriptor.DisplayName;
    public string Description => Descriptor.Description;
    public string Glyph => Descriptor.Glyph;
    public ICommand AddCommand { get; }
    public string AddLabel => "Add " + DisplayName;
    public string AddToolTip => $"Add to the {Descriptor.DefaultZone.ToString().ToLowerInvariant()} zone";

    /// <summary>False for a single-instance type that is on the bar already.</summary>
    public bool CanAdd
    {
        get => _canAdd;
        internal set
        {
            if (SetProperty(ref _canAdd, value))
            {
                OnPropertyChanged(nameof(IsAlreadyOnBar));
            }
        }
    }

    /// <summary>Shows the "Already on bar" pill in place of the Add button.</summary>
    public bool IsAlreadyOnBar => !_canAdd;
}

/// <summary>
/// One widget on the bar: its tile (icon, name, zone, on/off, selection) and its inspector layer (zone, slot width,
/// settings, remove). Templates have no ancestor bindings, so it carries its own commands.
/// </summary>
public sealed class WidgetItemViewModel : ObservableObject
{
    private const double OffOpacity = 0.55;

    private readonly WidgetsPageViewModel _owner;
    private readonly ISettingsService _settings;
    private readonly Debouncer _debounce;
    private bool _enabled;
    private double _width;
    private bool _widthPending;
    private TaskbarZone? _zone;
    private bool _isSelected;

    public WidgetItemViewModel(
        WidgetsPageViewModel owner,
        ISettingsService settings,
        IWidgetSettingsEditors editors,
        IUiDispatcher dispatcher,
        WidgetInstanceSettings instance,
        WidgetDescriptor descriptor)
    {
        _owner = owner;
        _settings = settings;
        _debounce = new Debouncer(dispatcher, TimeSpan.FromMilliseconds(120));
        InstanceId = instance.InstanceId;
        Descriptor = descriptor;
        _enabled = instance.Enabled;
        _width = descriptor.ResolveWidth(instance.Width);
        SelectCommand = new RelayCommand(() => _owner.Select(this));
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));

        // A registered custom editor wins; otherwise the form is generated from the [Setting] attributes.
        var context = new WidgetSettingsEditContext(settings, InstanceId, descriptor);
        CustomEditor = descriptor.SettingsType is null ? null : editors.Create(context);
        Fields = CustomEditor is null && descriptor.SettingsSchema is { } schema
            ? schema.Fields.Select(f => SettingFieldViewModel.Create(context, f, dispatcher)).ToList()
            : [];
    }

    public string InstanceId { get; }
    public WidgetDescriptor Descriptor { get; }
    public string DisplayName => Descriptor.DisplayName;
    public string Description => Descriptor.Description;
    public string Glyph => Descriptor.Glyph;
    public double MinWidth => Descriptor.MinWidth;
    public double MaxWidth => Descriptor.MaxWidth;

    /// <summary>Only fixed-size widgets have a slot width to choose; the others follow their content.</summary>
    public bool CanResize => Descriptor.Sizing == WidgetSizing.Fixed;

    // ----- Tile -----

    public ICommand SelectCommand { get; }

    /// <summary>True for the widget whose settings the inspector shows.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(SelectionOpacity));
                OnPropertyChanged(nameof(SelectName));
            }
        }
    }

    /// <summary>The selection ring's opacity. An opacity, not a visibility, so selecting never causes a layout pass.</summary>
    public double SelectionOpacity => _isSelected ? 1 : 0;

    /// <summary>The tile's content is dimmed while the widget is switched off.</summary>
    public double ContentOpacity => _enabled ? 1 : OffOpacity;

    public string ZoneLabel => _zone switch
    {
        TaskbarZone.Left => "Left zone",
        TaskbarZone.Center => "Center zone",
        TaskbarZone.Right => "Right zone",
        _ => "Not on the bar",
    };

    /// <summary>What a screen reader says for the tile.</summary>
    public string SelectName => _isSelected
        ? $"{DisplayName}, {ZoneLabel}, selected"
        : $"{DisplayName}, {ZoneLabel}";

    public string EnabledName => $"Show {DisplayName} on the bar";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(ContentOpacity));
                _settings.Update(s =>
                {
                    if (Find(s) is { } w)
                    {
                        w.Enabled = value;
                    }
                });
            }
        }
    }

    // ----- Inspector -----

    /// <summary>The id of the layout entry that places this widget; null when the layout has none.</summary>
    internal string? PlacementId { get; private set; }

    public bool IsPlaced => _zone is not null;

    /// <summary>The inspector's placement group (zone, slot width) has something to show.</summary>
    public bool HasPlacementControls => IsPlaced || CanResize;

    /// <summary>0 Left, 1 Center, 2 Right; -1 when the widget has no place in the layout. Setting it moves the widget.</summary>
    public int ZoneIndex
    {
        get => _zone switch
        {
            TaskbarZone.Left => 0,
            TaskbarZone.Center => 1,
            TaskbarZone.Right => 2,
            _ => -1,
        };
        set
        {
            // A combo box reports -1 while it has no selection; that is never a request to move.
            if (_zone is null || value < 0 || value > 2)
            {
                return;
            }

            var zone = value switch
            {
                0 => TaskbarZone.Left,
                1 => TaskbarZone.Center,
                _ => TaskbarZone.Right,
            };
            if (zone != _zone)
            {
                SetZone(zone);
                _owner.MoveToZone(this, zone);
            }
        }
    }

    public double Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, Math.Round(value)))
            {
                OnPropertyChanged(nameof(WidthLabel));
                var w = _width;
                _widthPending = true;
                _debounce.Run(() =>
                {
                    _widthPending = false;
                    _settings.Update(s =>
                    {
                        if (Find(s) is { } instance)
                        {
                            instance.Width = w;
                        }
                    });
                });
            }
        }
    }

    public string WidthLabel => AppearancePageViewModel.FormatPx(_width);

    /// <summary>The custom settings editor (a UI element), or null.</summary>
    public object? CustomEditor { get; }

    public bool HasCustomEditor => CustomEditor is not null;

    /// <summary>The generated settings form; empty when the widget has no settings or a custom editor.</summary>
    public IReadOnlyList<SettingFieldViewModel> Fields { get; }

    public bool HasGeneratedForm => Fields.Count > 0;

    public bool HasNoSettings => !HasCustomEditor && !HasGeneratedForm;

    public ICommand RemoveCommand { get; }

    public string RemoveLabel => $"Remove {DisplayName} from the bar";

    /// <summary>
    /// Takes over what the settings hold now (another page, the bar or a reset may have changed it) without writing
    /// anything back. Values with an edit of this page still queued are left alone: they are newer.
    /// </summary>
    internal void Sync(WidgetInstanceSettings instance, SectionPlacement? placement)
    {
        PlacementId = placement?.Id;
        SetZone(placement?.Zone);

        if (instance.Enabled != _enabled)
        {
            _enabled = instance.Enabled;
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(ContentOpacity));
        }

        var width = Descriptor.ResolveWidth(instance.Width);
        if (!_widthPending && width != _width)
        {
            _width = width;
            OnPropertyChanged(nameof(Width));
            OnPropertyChanged(nameof(WidthLabel));
        }

        foreach (var field in Fields)
        {
            field.Refresh();
        }
    }

    private void SetZone(TaskbarZone? zone)
    {
        if (zone == _zone)
        {
            return;
        }

        _zone = zone;
        OnPropertyChanged(nameof(ZoneIndex));
        OnPropertyChanged(nameof(ZoneLabel));
        OnPropertyChanged(nameof(SelectName));
        OnPropertyChanged(nameof(IsPlaced));
        OnPropertyChanged(nameof(HasPlacementControls));
    }

    private WidgetInstanceSettings? Find(SuperBarSettings s) => s.Widgets.FirstOrDefault(x => x.InstanceId == InstanceId);
}

/// <summary>One field of a generated settings form. Changes are saved through the settings service, which updates the running widget live.</summary>
public abstract class SettingFieldViewModel : ObservableObject
{
    private protected SettingFieldViewModel(WidgetSettingsEditContext context, SettingField field)
    {
        Context = context;
        Field = field;
    }

    public string Label => Field.Label;
    public string Description => Field.Description;
    public bool HasDescription => Field.Description.Length > 0;

    private protected WidgetSettingsEditContext Context { get; }
    private protected SettingField Field { get; }

    /// <summary>
    /// Re-reads the stored value and shows it, without writing. A no-op after this field's own write; skipped while
    /// an edit is still queued.
    /// </summary>
    internal abstract void Refresh();

    internal static SettingFieldViewModel Create(WidgetSettingsEditContext context, SettingField field, IUiDispatcher dispatcher) => field.Kind switch
    {
        SettingKind.Toggle => new ToggleSettingViewModel(context, field),
        SettingKind.Number => new NumberSettingViewModel(context, field, dispatcher),
        SettingKind.Choice => new ChoiceSettingViewModel(context, field),
        _ => new TextSettingViewModel(context, field, dispatcher),
    };
}

public sealed class ToggleSettingViewModel : SettingFieldViewModel
{
    private bool _value;

    internal ToggleSettingViewModel(WidgetSettingsEditContext context, SettingField field)
        : base(context, field) =>
        _value = context.Get(field) is true;

    public bool Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                Context.Set(Field, value);
            }
        }
    }

    internal override void Refresh()
    {
        var stored = Context.Get(Field) is true;
        if (stored != _value)
        {
            _value = stored;
            OnPropertyChanged(nameof(Value));
        }
    }
}

public sealed class NumberSettingViewModel : SettingFieldViewModel
{
    private readonly Debouncer _debounce;
    private double _value;
    private bool _pending;

    internal NumberSettingViewModel(WidgetSettingsEditContext context, SettingField field, IUiDispatcher dispatcher)
        : base(context, field)
    {
        _debounce = new Debouncer(dispatcher, TimeSpan.FromMilliseconds(250));
        _value = Convert.ToDouble(context.Get(field) ?? 0, CultureInfo.InvariantCulture);
    }

    public bool HasRange => Field.HasRange;
    public bool UsesNumberBox => !Field.HasRange;
    public double Minimum => Field.Minimum ?? double.MinValue;
    public double Maximum => Field.Maximum ?? double.MaxValue;
    public double Step => Field.Step ?? (Field.IsInteger ? 1 : 0.1);
    public string ValueLabel => Field.Unit.Length > 0 ? $"{_value:0.##} {Field.Unit}" : _value.ToString("0.##", CultureInfo.CurrentCulture);

    public double Value
    {
        get => _value;
        set
        {
            if (double.IsNaN(value))
            {
                return; // a cleared number box
            }

            var coerced = Convert.ToDouble(WidgetSettingsSchema.Coerce(Field, value), CultureInfo.InvariantCulture);
            if (SetProperty(ref _value, coerced))
            {
                OnPropertyChanged(nameof(ValueLabel));
                _pending = true;
                _debounce.Run(() =>
                {
                    _pending = false;
                    Context.Set(Field, coerced);
                });
            }
        }
    }

    internal override void Refresh()
    {
        if (_pending)
        {
            return;
        }

        var stored = Convert.ToDouble(Context.Get(Field) ?? 0, CultureInfo.InvariantCulture);
        if (stored != _value)
        {
            _value = stored;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValueLabel));
        }
    }
}

public sealed class ChoiceSettingViewModel : SettingFieldViewModel
{
    private int _selectedIndex;

    internal ChoiceSettingViewModel(WidgetSettingsEditContext context, SettingField field)
        : base(context, field) =>
        _selectedIndex = StoredIndex();

    public IReadOnlyList<string> Options => Field.Choices.Select(c => c.Label).ToList();

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value >= 0 && value < Field.Choices.Count && SetProperty(ref _selectedIndex, value))
            {
                Context.Set(Field, Field.Choices[value].Value);
            }
        }
    }

    internal override void Refresh()
    {
        var stored = StoredIndex();
        if (stored != _selectedIndex)
        {
            _selectedIndex = stored;
            OnPropertyChanged(nameof(SelectedIndex));
        }
    }

    private int StoredIndex()
    {
        var current = Context.Get(Field);
        return Math.Max(0, Field.Choices.ToList().FindIndex(c => Equals(c.Value, current)));
    }
}

public sealed class TextSettingViewModel : SettingFieldViewModel
{
    private readonly Debouncer _debounce;
    private string _value;
    private bool _pending;

    internal TextSettingViewModel(WidgetSettingsEditContext context, SettingField field, IUiDispatcher dispatcher)
        : base(context, field)
    {
        _debounce = new Debouncer(dispatcher, TimeSpan.FromMilliseconds(400));
        _value = context.Get(field) as string ?? "";
    }

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value ?? ""))
            {
                var text = _value;
                _pending = true;
                _debounce.Run(() =>
                {
                    _pending = false;
                    Context.Set(Field, text);
                });
            }
        }
    }

    internal override void Refresh()
    {
        if (_pending)
        {
            return;
        }

        var stored = Context.Get(Field) as string ?? "";
        if (stored != _value)
        {
            _value = stored;
            OnPropertyChanged(nameof(Value));
        }
    }
}

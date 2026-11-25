using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
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

/// <summary>
/// The Widgets page: the instances on the bar (enable, width, settings, remove) and the types that can be added.
/// </summary>
public sealed class WidgetsPageViewModel : PageViewModel
{
    private readonly ISettingsService _settings;
    private readonly IWidgetRegistry _registry;
    private readonly IWidgetSettingsEditors _editors;
    private readonly ISettingsStore _store;
    private readonly IUiDispatcher _dispatcher;
    private bool _isEmpty;

    public WidgetsPageViewModel(ISettingsService settings, IWidgetRegistry registry, IWidgetSettingsEditors editors, ISettingsStore store, IUiDispatcher dispatcher)
        : base("Widgets", "\uE71D", "Widgets live directly inside the bar. Add them, size their slots and change their settings.")
    {
        _settings = settings;
        _registry = registry;
        _editors = editors;
        _store = store;
        _dispatcher = dispatcher;
        Reload();
    }

    public ObservableCollection<WidgetItemViewModel> Instances { get; } = new();

    public ObservableCollection<WidgetTypeViewModel> Types { get; } = new();

    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }

    public override void OnNavigatedTo() => Reload();

    internal void Add(WidgetDescriptor descriptor)
    {
        _settings.Update(s => WidgetInstances.Add(s, descriptor.TypeId, descriptor.DefaultZone, descriptor.AllowMultiple));
        Reload();
    }

    internal void Remove(WidgetItemViewModel item)
    {
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
    }

    private void Reload()
    {
        var current = _settings.Current;
        Instances.Clear();
        foreach (var w in current.Widgets)
        {
            if (_registry.TryGet(w.TypeId, out var d))
            {
                var zone = current.Layout.FirstOrDefault(p => p.Kind == SectionKind.Widget && p.WidgetInstanceId == w.InstanceId)?.Zone;
                Instances.Add(new WidgetItemViewModel(this, _settings, _editors, _dispatcher, w, d, zone));
            }
        }

        Types.Clear();
        foreach (var d in _registry.All.OrderBy(d => d.DisplayName, StringComparer.CurrentCulture))
        {
            var onBar = current.Widgets.Any(w => w.TypeId == d.TypeId);
            Types.Add(new WidgetTypeViewModel(this, d, canAdd: d.AllowMultiple || !onBar));
        }

        IsEmpty = Instances.Count == 0;
    }
}

/// <summary>A widget type that can be added to the bar.</summary>
public sealed class WidgetTypeViewModel
{
    public WidgetTypeViewModel(WidgetsPageViewModel owner, WidgetDescriptor descriptor, bool canAdd)
    {
        Descriptor = descriptor;
        CanAdd = canAdd;
        AddCommand = new RelayCommand(_ => owner.Add(descriptor), _ => canAdd);
    }

    public WidgetDescriptor Descriptor { get; }
    public string DisplayName => Descriptor.DisplayName;
    public string Description => Descriptor.Description;
    public string Glyph => Descriptor.Glyph;
    public bool CanAdd { get; }
    public ICommand AddCommand { get; }
    public string AddLabel => "Add " + DisplayName;

    public string AvailabilityHint => CanAdd
        ? $"Added to the {Descriptor.DefaultZone.ToString().ToLowerInvariant()} zone"
        : "Already on the bar (only one allowed)";
}

/// <summary>One widget instance: enable, width, settings and remove.</summary>
public sealed class WidgetItemViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly Debouncer _debounce;
    private bool _enabled;
    private double _width;

    public WidgetItemViewModel(
        WidgetsPageViewModel owner,
        ISettingsService settings,
        IWidgetSettingsEditors editors,
        IUiDispatcher dispatcher,
        WidgetInstanceSettings instance,
        WidgetDescriptor descriptor,
        TaskbarZone? zone)
    {
        _settings = settings;
        _debounce = new Debouncer(dispatcher, TimeSpan.FromMilliseconds(120));
        InstanceId = instance.InstanceId;
        Descriptor = descriptor;
        _enabled = instance.Enabled;
        _width = descriptor.ResolveWidth(instance.Width);
        ZoneLabel = zone is { } z ? $"{z} zone" : "Not placed on the bar";
        RemoveCommand = new RelayCommand(() => owner.Remove(this));

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
    public string ZoneLabel { get; }
    public double MinWidth => Descriptor.MinWidth;
    public double MaxWidth => Descriptor.MaxWidth;

    /// <summary>Only fixed-size widgets have a slot width to choose; the others follow their content.</summary>
    public bool CanResize => Descriptor.Sizing == WidgetSizing.Fixed;

    public ICommand RemoveCommand { get; }
    public string RemoveLabel => "Remove " + DisplayName;

    /// <summary>The custom settings editor (a UI element), or null.</summary>
    public object? CustomEditor { get; }

    public bool HasCustomEditor => CustomEditor is not null;

    /// <summary>The generated settings form; empty when the widget has no settings or a custom editor.</summary>
    public IReadOnlyList<SettingFieldViewModel> Fields { get; }

    public bool HasGeneratedForm => Fields.Count > 0;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
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

    public double Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, Math.Round(value)))
            {
                OnPropertyChanged(nameof(WidthLabel));
                var w = _width;
                _debounce.Run(() => _settings.Update(s =>
                {
                    if (Find(s) is { } instance)
                    {
                        instance.Width = w;
                    }
                }));
            }
        }
    }

    public string WidthLabel => AppearancePageViewModel.FormatPx(_width);

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
}

public sealed class NumberSettingViewModel : SettingFieldViewModel
{
    private readonly Debouncer _debounce;
    private double _value;

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
                _debounce.Run(() => Context.Set(Field, coerced));
            }
        }
    }
}

public sealed class ChoiceSettingViewModel : SettingFieldViewModel
{
    private int _selectedIndex;

    internal ChoiceSettingViewModel(WidgetSettingsEditContext context, SettingField field)
        : base(context, field)
    {
        var current = context.Get(field);
        _selectedIndex = Math.Max(0, field.Choices.ToList().FindIndex(c => Equals(c.Value, current)));
    }

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
}

public sealed class TextSettingViewModel : SettingFieldViewModel
{
    private readonly Debouncer _debounce;
    private string _value;

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
                _debounce.Run(() => Context.Set(Field, text));
            }
        }
    }
}

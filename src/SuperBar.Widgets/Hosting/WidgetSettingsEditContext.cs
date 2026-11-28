using System.Text.Json.Nodes;
using SuperBar.Core.Settings;

namespace SuperBar.Widgets;

/// <summary>
/// How a settings editor (the Control Center's generated form, or a custom editor marked
/// <c>[WidgetSettingsViewFor]</c>) reads and changes one widget instance's settings. Every change is persisted
/// through the settings service, which is what delivers it live to the running widget. UI-framework free.
/// </summary>
public sealed class WidgetSettingsEditContext
{
    private readonly ISettingsService _settings;

    public WidgetSettingsEditContext(ISettingsService settings, string instanceId, WidgetDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);
        _settings = settings;
        InstanceId = instanceId;
        Descriptor = descriptor;
    }

    public string InstanceId { get; }

    public WidgetDescriptor Descriptor { get; }

    public WidgetSettingsSchema? Schema => Descriptor.SettingsSchema;

    /// <summary>A copy of the instance's stored options.</summary>
    public JsonObject Options =>
        Find(_settings.Current) is { } instance ? (JsonObject)instance.Options.DeepClone() : new JsonObject();

    /// <summary>The current value of a generated-form field.</summary>
    public object? Get(SettingField field) => RequireSchema().Read(Options, field);

    /// <summary>Stores a generated-form field's value (coerced to its type and range).</summary>
    public void Set(SettingField field, object? value)
    {
        var schema = RequireSchema();
        _settings.Update(s =>
        {
            if (Find(s) is { } instance)
            {
                schema.Write(instance.Options, field, value);
            }
        });
    }

    /// <summary>The instance's settings as their typed class (for custom editors).</summary>
    public TSettings Load<TSettings>() where TSettings : class, new() => WidgetSettingsBinder.Bind<TSettings>(Options);

    /// <summary>Changes the typed settings and persists them; keys the class does not know are kept.</summary>
    public void Save<TSettings>(Action<TSettings> change) where TSettings : class, new()
    {
        ArgumentNullException.ThrowIfNull(change);
        _settings.Update(s =>
        {
            if (Find(s) is { } instance)
            {
                var typed = WidgetSettingsBinder.Bind<TSettings>(instance.Options);
                change(typed);
                instance.Options = WidgetOptions.Merge(instance.Options, WidgetSettingsBinder.ToJson(typed));
            }
        });
    }

    private WidgetSettingsSchema RequireSchema() =>
        Schema ?? throw new InvalidOperationException($"Widget type {Descriptor.TypeId} has no [Setting] fields.");

    private WidgetInstanceSettings? Find(SuperBarSettings settings) =>
        settings.Widgets.FirstOrDefault(w => w.InstanceId == InstanceId);
}

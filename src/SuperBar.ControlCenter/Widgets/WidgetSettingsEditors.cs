using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using SuperBar.ControlCenter.ViewModels;
using SuperBar.Widgets;

namespace SuperBar.ControlCenter.Widgets;

/// <summary>
/// Marks a custom settings editor for a widget settings class. The Control Center shows it instead of the form
/// generated from <see cref="SettingAttribute"/>s. The editor is a UI element whose constructor takes a
/// <see cref="WidgetSettingsEditContext"/> (plus any services); it reads with <c>Load&lt;TSettings&gt;()</c> and
/// writes with <c>Save&lt;TSettings&gt;(...)</c>, and every save reaches the running widget live.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WidgetSettingsViewForAttribute(Type settingsType) : Attribute
{
    public Type SettingsType { get; } = settingsType;
}

/// <summary>Creates the custom settings editor for <see cref="SettingsType"/>.</summary>
public sealed record WidgetSettingsViewRegistration(Type SettingsType, Func<IServiceProvider, WidgetSettingsEditContext, UIElement> Factory);

public static class WidgetSettingsViewServiceCollectionExtensions
{
    /// <summary>Discovers every class marked with <see cref="WidgetSettingsViewForAttribute"/> in <paramref name="assembly"/>.</summary>
    public static IServiceCollection AddWidgetSettingsViewsFrom(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<WidgetSettingsViewForAttribute>() is not { } attribute)
            {
                continue;
            }

            if (type.IsAbstract || !typeof(UIElement).IsAssignableFrom(type))
            {
                throw new InvalidOperationException($"[WidgetSettingsViewFor] type {type.FullName} must be a non-abstract UIElement.");
            }

            var viewType = type;
            services.AddSingleton(new WidgetSettingsViewRegistration(
                attribute.SettingsType,
                (sp, context) => (UIElement)ActivatorUtilities.CreateInstance(sp, viewType, context)));
        }

        return services;
    }
}

/// <summary>Looks up and creates custom settings editors. UI thread only.</summary>
public sealed class WidgetSettingsEditors(
    IEnumerable<WidgetSettingsViewRegistration> registrations,
    IServiceProvider services,
    ILogger<WidgetSettingsEditors> logger) : IWidgetSettingsEditors
{
    private readonly Dictionary<Type, WidgetSettingsViewRegistration> _registrations = Index(registrations);

    public object? Create(WidgetSettingsEditContext context)
    {
        if (context.Descriptor.SettingsType is not { } settingsType || !_registrations.TryGetValue(settingsType, out var registration))
        {
            return null;
        }

        try
        {
            return registration.Factory(services, context);
        }
        catch (Exception ex)
        {
            // Better a generated form than no settings at all.
            logger.LogError(ex, "The custom settings editor for {SettingsType} failed; showing the generated form", settingsType.Name);
            return null;
        }
    }

    private static Dictionary<Type, WidgetSettingsViewRegistration> Index(IEnumerable<WidgetSettingsViewRegistration> registrations)
    {
        var index = new Dictionary<Type, WidgetSettingsViewRegistration>();
        foreach (var registration in registrations)
        {
            if (!index.TryAdd(registration.SettingsType, registration))
            {
                throw new InvalidOperationException($"More than one settings editor is registered for {registration.SettingsType.FullName}.");
            }
        }

        return index;
    }
}

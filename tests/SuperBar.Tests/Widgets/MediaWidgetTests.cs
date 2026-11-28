using Microsoft.Extensions.DependencyInjection;
using SuperBar.Core.Settings;
using SuperBar.Core.Shell;
using SuperBar.Widgets;
using SuperBar.Widgets.BuiltIn;

namespace SuperBar.Tests.Widgets;

/// <summary>A media session the test drives: what plays, and which commands were sent.</summary>
internal sealed class FakeMediaService : IMediaSessionService
{
    public MediaSnapshot Current { get; private set; } = MediaSnapshot.None;
    public int PlayPauseCount { get; private set; }
    public int NextCount { get; private set; }
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    public event EventHandler<MediaSnapshot>? Changed;

    public void Publish(MediaSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    public Task TogglePlayPauseAsync()
    {
        PlayPauseCount++;
        return Task.CompletedTask;
    }

    public Task NextAsync()
    {
        NextCount++;
        return Task.CompletedTask;
    }
}

internal sealed class SettableClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class MediaWidgetTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 21, 0, 0, TimeSpan.Zero);

    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeMediaService _media = new();
    private readonly SettableClock _clock = new(T0);
    private readonly WidgetHost _host;

    public MediaWidgetTests() => _host = Host();

    public void Dispose() => _host.Dispose();

    private WidgetHost Host(params (string Key, bool Value)[] options)
    {
        var settings = new WidgetInstanceSettings { InstanceId = "media-1", TypeId = "superbar.media" };
        foreach (var (key, value) in options)
        {
            settings.Options[key] = value;
        }

        return TestHost.Create(
            _ui,
            WidgetDescriptor.FromType(typeof(MediaWidget)),
            settings,
            configure: s => s.AddSingleton<IMediaSessionService>(_media).AddSingleton<TimeProvider>(_clock));
    }

    private MediaModel Model => Assert.IsType<MediaModel>(_host.View);

    private static MediaSnapshot Song(bool playing = true, int positionSeconds = 30, int durationSeconds = 200, DateTimeOffset? at = null, string key = "art1") =>
        new(true, playing, "Teardrop", "Massive Attack", TimeSpan.FromSeconds(positionSeconds), TimeSpan.FromSeconds(durationSeconds), at ?? T0, [1, 2, 3], key);

    [Fact]
    public void It_is_discovered_with_its_sizing_and_settings()
    {
        var registry = new ServiceCollection().AddSuperBarWidgets().BuildServiceProvider().GetRequiredService<IWidgetRegistry>();

        Assert.True(registry.TryGet("superbar.media", out var d));
        Assert.Equal("Now Playing", d.DisplayName);
        Assert.False(d.AllowMultiple);
        Assert.Equal((250.0, 180.0, 340.0), (d.DefaultWidth, d.MinWidth, d.MaxWidth));
        Assert.Equal(["hideWhenIdle"], d.SettingsSchema!.Fields.Select(f => f.Key));
        Assert.Equal(true, d.SettingsSchema.Fields[0].DefaultValue);
    }

    [Fact]
    public void It_shows_what_is_playing()
    {
        _media.Publish(Song());

        _host.Activate();

        Assert.Equal("Teardrop", Model.Title);
        Assert.Equal("Massive Attack", Model.Artist);
        Assert.True(Model.IsPlaying);
        Assert.True(Model.HasProgress);
        Assert.Equal(30.0 / 200, Model.Progress, 6);
        Assert.Equal("art1", Model.ThumbnailKey);
        Assert.Equal([1, 2, 3], Model.Thumbnail);
        Assert.False(Model.IsCollapsed);
    }

    [Fact]
    public void Updates_while_active_are_shown()
    {
        _host.Activate();

        _media.Publish(Song() with { Title = "Angel", ThumbnailKey = "art2", ThumbnailBytes = [9] });

        Assert.Equal("Angel", Model.Title);
        Assert.Equal("art2", Model.ThumbnailKey);
        Assert.Equal([9], Model.Thumbnail);
    }

    [Fact]
    public void While_playing_the_progress_advances_every_second_from_the_last_report()
    {
        _media.Publish(Song(positionSeconds: 30, at: T0));
        _host.Activate();
        var tick = Assert.Single(_ui.LiveTimers);
        Assert.Equal(MediaWidget.TickInterval, tick.Interval);

        _clock.Now = T0.AddSeconds(10);
        tick.Fire();

        Assert.Equal(40.0 / 200, Model.Progress, 6);
        Assert.Single(_ui.LiveTimers); // re-armed, not stacked
    }

    [Fact]
    public void A_new_timeline_report_snaps_the_progress()
    {
        _media.Publish(Song(positionSeconds: 30, at: T0));
        _host.Activate();
        _clock.Now = T0.AddSeconds(10);

        _media.Publish(Song(positionSeconds: 100, at: T0.AddSeconds(9))); // the user seeked

        Assert.Equal(101.0 / 200, Model.Progress, 6);
    }

    [Fact]
    public void Paused_media_does_not_tick_and_keeps_its_position()
    {
        _media.Publish(Song(playing: false, positionSeconds: 50, at: T0));
        _clock.Now = T0.AddMinutes(5);

        _host.Activate();

        Assert.Equal(50.0 / 200, Model.Progress, 6);
        Assert.Empty(_ui.LiveTimers);
        Assert.False(Model.IsPlaying);
    }

    [Fact]
    public void Pausing_freezes_the_progress_and_resuming_continues_from_there()
    {
        _media.Publish(Song(positionSeconds: 30, at: T0));
        _host.Activate();

        // Paused 20 s later; the app sends no new timeline (many don't).
        _clock.Now = T0.AddSeconds(20);
        _media.Publish(Song(playing: false, positionSeconds: 30, at: T0));
        Assert.Equal(50.0 / 200, Model.Progress, 6);
        _ = _ui.LiveTimers.Count; // the orphaned tick may still be pending; it does nothing
        foreach (var t in _ui.LiveTimers.ToList())
        {
            t.Fire();
        }

        Assert.Empty(_ui.LiveTimers);

        // A minute later it resumes: continue from 50 s, not from the stale report plus the paused time.
        _clock.Now = T0.AddSeconds(80);
        _media.Publish(Song(playing: true, positionSeconds: 30, at: T0));
        _clock.Now = T0.AddSeconds(85);
        Assert.Single(_ui.LiveTimers).Fire();

        Assert.Equal(55.0 / 200, Model.Progress, 6);
    }

    [Fact]
    public void Progress_never_passes_the_end()
    {
        _media.Publish(Song(positionSeconds: 195, durationSeconds: 200, at: T0));
        _host.Activate();

        _clock.Now = T0.AddSeconds(30);
        Assert.Single(_ui.LiveTimers).Fire();

        Assert.Equal(1.0, Model.Progress);
    }

    [Fact]
    public void Without_a_duration_the_progress_bar_is_hidden_and_nothing_ticks()
    {
        _media.Publish(Song(durationSeconds: 0) with { Duration = TimeSpan.Zero });

        _host.Activate();

        Assert.False(Model.HasProgress);
        Assert.Empty(_ui.LiveTimers);
    }

    [Fact]
    public void Play_pause_and_next_reach_the_media_session()
    {
        _media.Publish(Song());
        _host.Activate();

        Model.PlayPause.Execute();
        Model.Next.Execute();
        Model.Next.Execute();

        Assert.Equal(1, _media.PlayPauseCount);
        Assert.Equal(2, _media.NextCount);
    }

    [Fact]
    public void With_nothing_open_it_collapses_and_its_buttons_are_disabled()
    {
        _host.Activate();

        Assert.True(Model.IsCollapsed);
        Assert.False(Model.PlayPause.CanExecute());
        Assert.False(Model.Next.CanExecute());

        _media.Publish(Song());
        Assert.False(Model.IsCollapsed);
        Assert.True(Model.PlayPause.CanExecute());

        _media.Publish(MediaSnapshot.None); // the app closed
        Assert.True(Model.IsCollapsed);
        Assert.Equal("Nothing playing", Model.Title);
        Assert.False(Model.HasProgress);
    }

    [Fact]
    public void With_hide_when_idle_off_it_stays_on_the_bar()
    {
        using var host = Host(("hideWhenIdle", false));
        host.Activate();
        var model = Assert.IsType<MediaModel>(host.View);

        Assert.False(model.IsCollapsed);
        Assert.Equal("Nothing playing", model.Title);
    }

    [Fact]
    public void Turning_hide_when_idle_on_live_collapses_it()
    {
        using var host = Host(("hideWhenIdle", false));
        host.Activate();
        var model = Assert.IsType<MediaModel>(host.View);

        host.Apply(new WidgetInstanceSettings { InstanceId = "media-1", TypeId = "superbar.media", Options = { ["hideWhenIdle"] = true } });

        Assert.True(model.IsCollapsed);
    }

    [Fact]
    public void Deactivation_stops_listening_and_ticking()
    {
        _media.Publish(Song());
        _host.Activate();
        Assert.Equal(1, _media.Listeners);

        _host.Deactivate();

        Assert.Equal(0, _media.Listeners);
        Assert.Empty(_ui.LiveTimers);

        _media.Publish(Song() with { Title = "Angel" });
        Assert.Equal("Teardrop", Model.Title);

        _host.Activate(); // catches up
        Assert.Equal("Angel", Model.Title);
        Assert.Equal(1, _media.Listeners);
    }

    [Fact]
    public void Removal_leaves_nothing_behind()
    {
        _media.Publish(Song());
        _host.Activate();
        var model = Model;

        _host.Dispose();

        Assert.Equal(0, _media.Listeners);
        Assert.Empty(_ui.LiveTimers);
        Assert.False(model.PlayPause.CanExecute()); // a view that outlives the widget cannot reach it
        Assert.False(model.Next.CanExecute());
        model.Next.Execute();
        Assert.Equal(0, _media.NextCount);
    }
}

public class WidgetScopeListenTests
{
    private readonly FakeUiDispatcher _ui = new();

    [Fact]
    public void Events_raised_off_the_ui_thread_are_handled_on_it_behind_the_guard()
    {
        var media = new FakeMediaService();
        var seen = new List<string>();
        var widget = new ScriptedWidget
        {
            OnActivate = scope => scope.Listen<MediaSnapshot>(h => media.Changed += h, h => media.Changed -= h, s =>
            {
                seen.Add(s.Title);
                if (s.Title == "boom")
                {
                    throw new InvalidOperationException("handler failed");
                }
            }),
        };
        var host = TestHost.Create(_ui, (_, _) => widget);
        host.Activate();

        media.Publish(MediaSnapshot.None with { Title = "a" }); // the fake dispatcher says "on the UI thread": handled at once
        Assert.Equal(["a"], seen);

        media.Publish(MediaSnapshot.None with { Title = "boom" });
        Assert.Equal(WidgetState.Faulted, host.State); // the bar survives; the widget is isolated
        Assert.Equal(0, media.Listeners);              // and its subscription is gone with its scope
    }

    [Fact]
    public void Events_from_another_thread_are_queued_to_the_ui_thread()
    {
        var ui = new OffThreadDispatcher();
        var media = new FakeMediaService();
        var seen = new List<string>();
        var widget = new ScriptedWidget
        {
            OnActivate = scope => scope.Listen<MediaSnapshot>(h => media.Changed += h, h => media.Changed -= h, s => seen.Add(s.Title)),
        };
        var services = new ServiceCollection().AddSingleton<SuperBar.Core.Runtime.IUiDispatcher>(ui).BuildServiceProvider();
        var host = new WidgetHost(new WidgetDescriptor("t", "T", (_, _) => widget), new WidgetInstanceSettings { InstanceId = "w", TypeId = "t" }, services, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        host.Activate();

        media.Publish(MediaSnapshot.None with { Title = "queued" });
        Assert.Empty(seen);

        ui.Pump();
        Assert.Equal(["queued"], seen);
    }

    private sealed class OffThreadDispatcher : SuperBar.Core.Runtime.IUiDispatcher
    {
        private readonly Queue<Action> _queue = new();

        public bool HasThreadAccess => false;

        public bool TryEnqueue(Action action)
        {
            _queue.Enqueue(action);
            return true;
        }

        public SuperBar.Core.Runtime.IUiTimer CreateTimer(TimeSpan interval, Action tick) => new FakeTimer(interval, tick);

        public void Pump()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}

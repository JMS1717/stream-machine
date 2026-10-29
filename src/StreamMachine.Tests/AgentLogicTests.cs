using StreamMachine.Agent;
using StreamMachine.Agent.Input;
using StreamMachine.Agent.Overlay;
using Xunit;

namespace StreamMachine.Tests;

/// <summary>Simulated controller presses through the gesture engine, with exact timing.</summary>
public class GestureTests
{
    static PadState S(Pad b, float lx = 0, float ly = 0) => new(b, lx, ly, 0, 0, 0, 0, "sim");

    /// <summary>Holds <paramref name="b"/> from t0 for <paramref name="ms"/> at 16 ms polls; returns all events.</summary>
    static List<GestureEvent> Hold(GestureEngine g, Pad b, long t0, int ms, float lx = 0, float ly = 0)
    {
        var all = new List<GestureEvent>();
        for (long t = t0; t <= t0 + ms; t += 16) all.AddRange(g.Update(S(b, lx, ly), t));
        all.AddRange(g.Update(S(Pad.None), t0 + ms + 16));
        return all;
    }

    [Fact]
    public void View_plus_menu_held_one_second_opens_overlay_once()
    {
        var g = new GestureEngine();
        var e = Hold(g, Pad.View | Pad.Menu, 0, 1300);
        Assert.Single(e.OfType<OpenOverlay>());
        Assert.Equal("View+Menu", e.OfType<OpenOverlay>().Single().Reason);
    }

    [Fact]
    public void Short_view_plus_menu_does_nothing()
    {
        var g = new GestureEngine();
        Assert.Empty(Hold(g, Pad.View | Pad.Menu, 0, 900).OfType<OpenOverlay>());
    }

    [Fact]
    public void Guide_held_opens_only_when_enabled()
    {
        var g = new GestureEngine();
        Assert.Empty(Hold(g, Pad.Guide, 0, 300).OfType<OpenOverlay>());   // a tap stays the stream's
        Assert.Single(Hold(g, Pad.Guide, 5000, 600).OfType<OpenOverlay>());
        // As recorded on the 8BitDo: Guide held together with RB.
        Assert.Single(Hold(g, Pad.Guide | Pad.RB, 8000, 600).OfType<OpenOverlay>());
        g.GuideEnabled = false;
        Assert.Empty(Hold(g, Pad.Guide, 10000, 3000).OfType<OpenOverlay>());
    }

    [Fact]
    public void Nothing_counts_after_the_gesture_until_release()
    {
        var g = new GestureEngine();
        var events = new List<GestureEvent>();
        for (long t = 0; t <= 1100; t += 16) events.AddRange(g.Update(S(Pad.View | Pad.Menu), t));
        Assert.Single(events.OfType<OpenOverlay>());
        // Still holding, and also pressing A while letting go: ignored.
        Assert.Empty(g.Update(S(Pad.View | Pad.Menu | Pad.A), 1200));
        Assert.Empty(g.Update(S(Pad.A), 1216));
        Assert.Empty(g.Update(S(Pad.None), 1232));
        Assert.Equal(new Pressed(Pad.A), g.Update(S(Pad.A), 1248).Single());
    }

    [Fact]
    public void Dpad_and_stick_navigate_with_repeat()
    {
        var g = new GestureEngine();
        var e = Hold(g, Pad.Down, 0, 1000).OfType<Navigate>().ToList();
        Assert.Equal(new Navigate(Nav.Down, false), e[0]);
        // 450 ms delay then every 140 ms: repeats at ~450, 590, 730, 870 (+-1 poll)
        Assert.InRange(e.Count(x => x.Repeat), 3, 5);
        var stick = Hold(g, Pad.None, 5000, 100, ly: 0.9f).OfType<Navigate>().ToList();
        Assert.Equal(new Navigate(Nav.Up, false), stick.Single());
        Assert.Empty(Hold(g, Pad.None, 9000, 100, ly: 0.3f).OfType<Navigate>()); // inside the dead zone
    }

    [Fact]
    public void Buttons_report_press_edges_only()
    {
        var g = new GestureEngine();
        var e = Hold(g, Pad.A, 0, 500);
        Assert.Equal(new GestureEvent[] { new Pressed(Pad.A) }, e);
    }

    [Theory]
    [InlineData("view+menu", Pad.View | Pad.Menu)]
    [InlineData("back+start", Pad.View | Pad.Menu)]
    [InlineData("A", Pad.A)]
    [InlineData("lb,rb", Pad.LB | Pad.RB)]
    [InlineData("guide", Pad.Guide)]
    [InlineData("down", Pad.Down)]
    public void Parses_button_names(string text, Pad expect) => Assert.Equal(expect, PadState.ParseButtons(text));
}

public class OverlayModelTests
{
    [Fact]
    public void Navigation_moves_through_list_and_power_row()
    {
        var m = new OverlayModel();
        Assert.Equal(ItemId.Home, m.Selected.Id);
        m.Move(Nav.Up);                                   // wraps to the power row
        Assert.Equal(ItemId.Sleep, m.Selected.Id);
        m.Move(Nav.Right); Assert.Equal(ItemId.Restart, m.Selected.Id);
        m.Move(Nav.Right); Assert.Equal(ItemId.Shutdown, m.Selected.Id);
        m.Move(Nav.Right); Assert.Equal(ItemId.Sleep, m.Selected.Id);
        m.Move(Nav.Left); Assert.Equal(ItemId.Shutdown, m.Selected.Id);
        m.Move(Nav.Down); Assert.Equal(ItemId.Home, m.Selected.Id);
        for (int i = 0; i < 7; i++) m.Move(Nav.Down);
        Assert.Equal(ItemId.Health, m.Selected.Id);
        m.Move(Nav.Down); Assert.Equal(ItemId.Settings, m.Selected.Id);
        m.Move(Nav.Down); Assert.Equal(ItemId.Sleep, m.Selected.Id);
        m.Move(Nav.Up); Assert.Equal(ItemId.Settings, m.Selected.Id);
        m.Move(Nav.Up); Assert.Equal(ItemId.Health, m.Selected.Id);
        m.Move(Nav.Left); Assert.Equal(ItemId.Health, m.Selected.Id); // left/right do nothing in the list
    }

    [Fact]
    public void Only_one_item_selected_and_moving_disarms()
    {
        var m = new OverlayModel();
        m.Index = (int)ItemId.Shutdown;
        m.Selected.IsArmed = true;
        Assert.Equal("Press A again to shut down", m.Selected.DisplayTitle);
        m.Move(Nav.Left);
        Assert.Single(m.All.Where(i => i.IsSelected));
        Assert.Empty(m.All.Where(i => i.IsArmed));
    }
}

public class SettingsTests
{
    [Fact]
    public void Navigation_skips_section_headers_and_wraps()
    {
        var s = new SettingsModel();
        Assert.Equal("client", s.Selected.Key);
        s.Move(-1);                                         // past the first header to the last row
        Assert.Equal("version", s.Selected.Key);
        s.Move(1);
        Assert.Equal("client", s.Selected.Key);
        Assert.Single(s.Items, i => i.IsSelected);
        for (int i = 0; i < s.Items.Count * 2; i++) { s.Move(1); Assert.False(s.Selected.IsHeader); }
    }

    [Fact]
    public void Values_step_clamp_wrap_and_snap()
    {
        var s = new SettingsModel();
        var hold = s.Item("guide-hold");
        hold.Show("400");
        Assert.Equal("0.4 s", hold.ValueLabel);
        Assert.True(hold.Step(1)); Assert.Equal("500", hold.Value);
        hold.Show("1000");
        Assert.False(hold.Step(1));                          // ranges stop at the end with left/right...
        Assert.True(hold.Next()); Assert.Equal("200", hold.Value); // ...A wraps around
        hold.Show("450");                                    // a hand-edited value snaps to the nearest option
        Assert.Contains(hold.Value, new[] { "400", "500" });

        var rumble = s.Item("rumble");
        rumble.Show("on");
        Assert.True(rumble.Step(1)); Assert.Equal("off", rumble.Value);
        Assert.True(rumble.Step(1)); Assert.Equal("on", rumble.Value); // on/off wraps both ways

        Assert.False(s.Item("version").Next());              // read-only
        Assert.False(s.Item("tv-setup").Next());             // an action, not a value
        Assert.True(s.Item("tv-setup").IsAction);
    }

    [Fact]
    public void Dynamic_options_replace_the_choices()
    {
        var host = new SettingsModel().Item("host");
        host.SetOptions([("GamingPC", "GamingPC"), ("Office", "Office"), ("Client default", "")], "Office");
        Assert.Equal("Office", host.ValueLabel);
        Assert.True(host.CanChange);
        host.SetOptions([("Client default", "")], "");
        Assert.False(host.CanChange);
    }

    [Fact]
    public void Every_service_setting_on_the_page_is_accepted_by_the_control_channel()
    {
        foreach (var item in new SettingsModel().Items.Where(i => i.Scope is SettingScope.Service or SettingScope.ServicePerTv))
            foreach (var o in item.Options)
            {
                Assert.True(StreamMachine.Core.Protocol.TryParse($"set {item.Key} {o.Value}", out StreamMachine.Core.Protocol.Request? _), $"{item.Key} {o.Value}");
                if (item.Scope == SettingScope.ServicePerTv)
                    Assert.True(StreamMachine.Core.Protocol.TryParse($"set {item.Key} {o.Value} 2", out StreamMachine.Core.Protocol.Request? _), $"{item.Key} {o.Value} 2");
            }
    }

    [Fact]
    public void Every_agent_setting_on_the_page_round_trips()
    {
        var a = new AgentSettings();
        foreach (var item in new SettingsModel().Items.Where(i => i.Scope == SettingScope.Agent && i.Key is not ("client" or "host")))
            foreach (var o in item.Options)
            {
                Assert.True(a.Apply(item.Key, o.Value), item.Key);
                Assert.Equal(o.Value, a.Get(item.Key));
            }
        a.GuideHoldMs = 99999; a.MouseSpeed = -5; a.OverlayTimeoutSeconds = -1; a.Client = "nope"; a.VolumeMax = 1000;
        a.Clamp();
        Assert.Equal(1500, a.GuideHoldMs);
        Assert.Equal(200, a.MouseSpeed);
        Assert.Equal(0, a.OverlayTimeoutSeconds);
        Assert.Equal("punktfunk", a.Client);
        Assert.Equal(100, a.VolumeMax);
    }
}

public class VolumeModelTests
{
    [Fact]
    public void Slider_fills_to_the_target_and_marks_where_the_tv_is()
    {
        var v = new VolumeModel { Max = 15 };
        Assert.Equal(15, v.Segments.Count);
        Assert.Equal("–", v.Label);                          // unknown until the TV reports
        v.Known = true;
        v.Tv = 4;
        v.Target = 9;
        Assert.Equal(9, v.Segments.Count(s => s.On));
        Assert.Equal(4, v.Segments.Single(s => s.IsTv).Number);
        Assert.Equal("TV is at 4, moving to 9", v.Status);
        v.Tv = 9;
        Assert.DoesNotContain(v.Segments, s => s.IsTv);
        v.Target = 99;
        Assert.Equal(15, v.Target);                          // clamped to the slider's range
        v.Max = 50;
        Assert.Equal(20, v.Segments.Count);                  // wide ranges use coarser bars
    }
}

public class InputMathTests
{
    [Theory]
    [InlineData(0, 8, Pad.Up)]
    [InlineData(2, 8, Pad.Right)]
    [InlineData(5, 8, Pad.Down | Pad.Left)]
    [InlineData(8, 8, Pad.None)]
    [InlineData(15, 8, Pad.None)]
    [InlineData(1, 4, Pad.Right)]
    public void Hat_switch(int v, int positions, Pad expect) => Assert.Equal(expect, RawHidSource.MapHat(v, positions));

    [Fact]
    public void Hid_axes_and_buttons()
    {
        Assert.Equal(-1f, RawHidSource.Normalize(0, 0, 255));
        Assert.Equal(1f, RawHidSource.Normalize(255, 0, 255));
        Assert.Equal(0f, RawHidSource.Normalize(128, 0, 255)); // centered within the dead zone
        Assert.Equal(Pad.A, RawHidSource.MapButton(1));
        Assert.Equal(Pad.Menu, RawHidSource.MapButton(8));
        Assert.Equal(Pad.None, RawHidSource.MapButton(20));
    }

    [Fact]
    public void Mouse_curve_has_dead_zone_and_is_fine_near_center()
    {
        Assert.Equal(0, MouseMode.Curve(0.1));
        Assert.Equal(1, MouseMode.Curve(1), 3);
        Assert.Equal(-1, MouseMode.Curve(-1), 3);
        Assert.True(MouseMode.Curve(0.5) < 0.5);
    }
}

using System.Collections.Generic;
using Godot;
using WebAirIG.Common.Truth;
using WebAirIG.IG.Sensors;

namespace WebAirIG.IG.Ui;

/// <summary>
/// Operator monitor in the IG window: 2x2 grid of sensor outputs with optional truth boxes and a status line.
/// Keys: T = toggle truth boxes, 1-4 = single camera, 0 = grid, H = toggle status text.
/// </summary>
public sealed partial class IgHud : CanvasLayer
{
    private readonly List<(SensorChannel Channel, PanelContainer Panel, TruthOverlay Overlay, Label Label)> _panels = new();
    private GridContainer _grid = null!;
    private Label _status = null!;
    private bool _showTruth = true;

    public void Build(IReadOnlyList<SensorChannel> channels)
    {
        var root = new VBoxContainer { Name = "HudRoot" };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(root);

        _status = new Label { Text = "WebAirIG", AutowrapMode = TextServer.AutowrapMode.Off };
        _status.AddThemeColorOverride("font_color", Colors.White);
        root.AddChild(_status);

        _grid = new GridContainer { Columns = 2, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _grid.AddThemeConstantOverride("h_separation", 4);
        _grid.AddThemeConstantOverride("v_separation", 4);
        root.AddChild(_grid);

        foreach (var ch in channels)
        {
            var panel = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            var tex = new TextureRect
            {
                Texture = ch.PostViewport.GetTexture(),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            };
            panel.AddChild(tex);
            var overlay = new TruthOverlay { ImageWidth = ch.Def.Width, ImageHeight = ch.Def.Height, MouseFilter = Control.MouseFilterEnum.Ignore };
            overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            tex.AddChild(overlay);
            var label = new Label { Text = ch.Def.Name, VerticalAlignment = VerticalAlignment.Top };
            label.AddThemeColorOverride("font_color", Colors.White);
            label.AddThemeColorOverride("font_outline_color", Colors.Black);
            label.AddThemeConstantOverride("outline_size", 4);
            tex.AddChild(label);
            _grid.AddChild(panel);
            _panels.Add((ch, panel, overlay, label));
        }
    }

    public void UpdateStatus(string status)
    {
        _status.Text = status;
        foreach (var (ch, _, overlay, label) in _panels)
        {
            var t = ch.LatestTruth;
            int n = t?.Objects.Count ?? 0;
            label.Text = $" {ch.Def.Name}  az {ch.AzDeg:F1}  el {ch.ElDeg:F1}  fov {ch.View.VfovDeg:F1}  objects {n}";
            overlay.Truth = _showTruth ? t : null;
            overlay.QueueRedraw();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        switch (key.Keycode)
        {
            case Key.T: _showTruth = !_showTruth; break;
            case Key.H: _status.Visible = !_status.Visible; break;
            case Key.Key0: Solo(-1); break;
            case Key.Key1: Solo(0); break;
            case Key.Key2: Solo(1); break;
            case Key.Key3: Solo(2); break;
            case Key.Key4: Solo(3); break;
        }
    }

    private void Solo(int index)
    {
        for (int i = 0; i < _panels.Count; i++) _panels[i].Panel.Visible = index < 0 || i == index;
        _grid.Columns = index < 0 ? 2 : 1;
    }
}

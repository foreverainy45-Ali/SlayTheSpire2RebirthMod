using Godot;

namespace SeedRestart;

internal sealed class RestartUi
{
    private readonly Node _owner;
    private CanvasLayer? _layer;
    private Label? _text;
    private string _key = "";

    // Use plain engine controls; no PCK, custom scenes, fonts or native popup windows required.
    public RestartUi(Node owner) => _owner = owner;
    public void Show(string key, string text, params (string Label, Action Action)[] actions)
    {
        if (_key == key && GodotObject.IsInstanceValid(_layer)) { _text!.Text = text; return; }
        Close();
        _key = key;
        _layer = new CanvasLayer { Name = "SeedRestartOverlay", Layer = 110 };
        var backdrop = new ColorRect { Color = new Color(0, 0, 0, 0.8f), MouseFilter = Control.MouseFilterEnum.Stop };
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(640, 240) };
        var margin = new MarginContainer();
        foreach (var side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" }) margin.AddThemeConstantOverride(side, 28);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 22);
        _text = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(590, 120) };
        _text.AddThemeFontSizeOverride("font_size", 25);
        column.AddChild(_text);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 24);
        foreach (var action in actions)
        {
            var button = new Button { Text = action.Label, CustomMinimumSize = new Vector2(145, 50) };
            button.AddThemeFontSizeOverride("font_size", 22);
            button.Pressed += () => action.Action();
            row.AddChild(button);
        }
        column.AddChild(row);
        margin.AddChild(column);
        panel.AddChild(margin);
        center.AddChild(panel);
        backdrop.AddChild(center);
        _layer.AddChild(backdrop);
        _owner.AddChild(_layer);
        if (row.GetChildCount() > 0) ((Button)row.GetChild(0)).GrabFocus();
    }

    public void Close()
    {
        if (GodotObject.IsInstanceValid(_layer))
        {
            _layer!.GetParent()?.RemoveChild(_layer);
            _layer.QueueFree();
        }
        _layer = null;
        _text = null;
        _key = "";
    }

}

using Godot;
using JmcModLib.Prefabs;
using MegaCrit.Sts2.Core.Logging;

namespace SeedRestart;

internal sealed class RestartUi
{
    private readonly Node _owner;
    private CanvasLayer? _layer;
    private Label? _text;
    private JmcReportPopupHandle? _loadingPopup;
    private string _key = "";
    internal bool NativeLoadingOpen
    {
        get
        {
            try { return _loadingPopup is { IsOpen: true }; }
            catch (ObjectDisposedException) { return false; }
        }
    }
    internal string? LoadingStatus { get; private set; }
    internal string? LoadingQuote { get; private set; }

    // Use plain engine controls; no PCK, custom scenes, fonts or native popup windows required.
    public RestartUi(Node owner) => _owner = owner;
    public void Show(string key, string text, params (string Label, Action Action)[] actions)
    {
        CloseLoadingPopup();
        ShowOverlay(key, text, actions);
    }

    public void ShowLoading(string status, string? quote)
    {
        LoadingStatus = status;
        LoadingQuote = quote;
        string body = string.IsNullOrWhiteSpace(quote)
            ? status
            : $"{status}\n\n农神语录：{quote}";
        try
        {
            if (_loadingPopup is { IsOpen: true })
            {
                _loadingPopup.SetBody(body, JmcReportPopupBodyFormat.PlainText);
                _loadingPopup.SetStatus("请稍候");
                CloseOverlay();
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            // Scene teardown can dispose the native view before its handle observes closure.
            _loadingPopup = null;
        }
        catch (InvalidOperationException e)
        {
            Log.Warn("[SeedRestart] Native loading popup could not be updated; reopening or using fallback: " + e.Message);
            _loadingPopup = null;
        }
        _loadingPopup = null;
        try
        {
            if (JmcReportPopup.IsAvailable)
            {
                _loadingPopup = JmcReportPopup.Open(new JmcReportPopupOptions
                {
                    Title = "Rebirth · 快速重开",
                    Body = body,
                    Status = "请稍候",
                    BodyFormat = JmcReportPopupBodyFormat.PlainText,
                    Buttons = [new JmcReportPopupButton("waiting", "请稍候", _ => { }, false, false)],
                    ShowBackstop = true,
                    CloseOnEscape = false,
                    MinimumSize = new Vector2(700, 300)
                }, typeof(RestartUi).Assembly);
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            Log.Warn("[SeedRestart] Native loading popup unavailable during scene transition; using fallback: " + e.Message);
            _loadingPopup = null;
        }
        if (_loadingPopup != null)
        {
            CloseOverlay();
            return;
        }
        ShowOverlay("loading-fallback", body);
    }

    private void ShowOverlay(string key, string text, params (string Label, Action Action)[] actions)
    {
        if (_key == key && GodotObject.IsInstanceValid(_layer)) { _text!.Text = text; return; }
        CloseOverlay();
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
        CloseLoadingPopup();
        CloseOverlay();
    }

    private void CloseLoadingPopup()
    {
        try
        {
            if (_loadingPopup is { IsOpen: true }) _loadingPopup.Close();
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            Log.Debug("[SeedRestart] Native loading popup was already released: " + e.Message);
        }
        _loadingPopup = null;
        LoadingStatus = null;
        LoadingQuote = null;
    }

    private void CloseOverlay()
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

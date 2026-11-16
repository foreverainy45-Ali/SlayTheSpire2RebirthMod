using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace SeedRestart;

internal sealed class RestartUi
{
    private readonly Node _owner;
    private CanvasLayer? _layer;
    private CanvasLayer? _loadingLayer;
    private Label? _text;
    private NGenericPopup? _loadingPopup;
    private NVerticalPopup? _loadingPanel;
    private MegaRichTextLabel? _quoteLabel;
    private string _key = "";
    internal bool NativeLoadingOpen
    {
        get
        {
            try
            {
                return GodotObject.IsInstanceValid(_loadingLayer) && _loadingLayer!.IsInsideTree() &&
                    GodotObject.IsInstanceValid(_loadingPopup) && _loadingPopup!.IsInsideTree();
            }
            catch (ObjectDisposedException) { return false; }
        }
    }
    internal string? LoadingStatus { get; private set; }
    internal string? LoadingQuote { get; private set; }
    internal string? RenderedQuote => GodotObject.IsInstanceValid(_quoteLabel) ? _quoteLabel!.Text : null;
    internal bool QuoteBottomCentered => GodotObject.IsInstanceValid(_quoteLabel) &&
        _quoteLabel!.HorizontalAlignment == HorizontalAlignment.Center &&
        _quoteLabel.VerticalAlignment == VerticalAlignment.Center &&
        _quoteLabel.AnchorTop >= 0.7f && _quoteLabel.AnchorBottom <= 0.91f;

    // Reuse the game's popup scene and fonts; the mod ships no PCK or copied game assets.
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
        string fallbackBody = string.IsNullOrWhiteSpace(quote)
            ? status
            : $"{status}\n\n{quote}";
        try
        {
            if (NativeLoadingOpen && GodotObject.IsInstanceValid(_loadingPanel))
            {
                _loadingPanel!.SetText("Rebirth · 快速重开", status);
                if (GodotObject.IsInstanceValid(_quoteLabel)) _quoteLabel!.Text = quote ?? "";
                CloseOverlay();
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            // Scene teardown can dispose the native view before its handle observes closure.
        }
        catch (InvalidOperationException e)
        {
            Log.Warn("[SeedRestart] Native loading popup could not be updated; reopening or using fallback: " + e.Message);
        }
        CloseNativePopup();
        try
        {
            CreatePersistentLoadingPopup(status, quote);
        }
        catch (Exception e)
        {
            Log.Warn("[SeedRestart] Native loading popup unavailable during scene transition; using fallback: " + e.Message);
            CloseNativePopup();
        }
        if (NativeLoadingOpen)
        {
            CloseOverlay();
            return;
        }
        ShowOverlay("loading-fallback", fallbackBody);
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
        CloseNativePopup();
        LoadingStatus = null;
        LoadingQuote = null;
    }

    private static void HideWaitingButtons(NVerticalPopup panel)
    {
        panel.YesButton.Visible = false;
        panel.NoButton.Visible = false;
        panel.YesButton.FocusMode = Control.FocusModeEnum.None;
        panel.NoButton.FocusMode = Control.FocusModeEnum.None;
        panel.YesButton.MouseFilter = Control.MouseFilterEnum.Ignore;
        panel.NoButton.MouseFilter = Control.MouseFilterEnum.Ignore;
    }

    private void CreatePersistentLoadingPopup(string status, string? quote)
    {
        // A scene-owned NModalContainer is destroyed while the run changes rooms. Keeping the
        // native popup under NGame lets the same stone panel remain visible through that gap.
        _loadingLayer = new CanvasLayer { Name = "SeedRestartLoading", Layer = 120 };
        var backdrop = new ColorRect
        {
            Name = "Backstop",
            Color = new Color(0, 0, 0, 0.8f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _loadingPopup = NGenericPopup.Create() ?? throw new InvalidOperationException("游戏原生提示框创建失败");
        _loadingPopup.Name = "NativeStonePopup";
        _loadingLayer.AddChild(backdrop);
        _loadingLayer.AddChild(_loadingPopup);
        _owner.AddChild(_loadingLayer);
        _loadingPanel = _loadingPopup.GetNode<NVerticalPopup>("VerticalPopup");
        _loadingPanel.SetText("Rebirth · 快速重开", status);
        HideWaitingButtons(_loadingPanel);
        _quoteLabel = CreateQuoteLabel(_loadingPanel, quote);
    }

    private static MegaRichTextLabel CreateQuoteLabel(NVerticalPopup panel, string? quote)
    {
        var bodyLabel = (MegaRichTextLabel?)AccessTools.Property(typeof(NVerticalPopup), "BodyLabel").GetValue(panel)
            ?? throw new InvalidOperationException("游戏原生提示框正文节点不可用");
        var label = (MegaRichTextLabel)bodyLabel.Duplicate();
        label.Name = "SeedRestartQuote";
        label.Text = quote ?? "";
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.AutoSizeEnabled = true;
        label.MinFontSize = 18;
        label.MaxFontSize = 26;
        label.MouseFilter = Control.MouseFilterEnum.Ignore;
        label.AnchorLeft = 0.12f;
        label.AnchorRight = 0.88f;
        label.AnchorTop = 0.72f;
        label.AnchorBottom = 0.9f;
        label.OffsetLeft = label.OffsetTop = label.OffsetRight = label.OffsetBottom = 0;
        panel.AddChild(label);
        return label;
    }

    private void CloseNativePopup()
    {
        try
        {
            if (GodotObject.IsInstanceValid(_loadingLayer))
            {
                _loadingLayer!.GetParent()?.RemoveChild(_loadingLayer);
                _loadingLayer.QueueFree();
            }
            else if (GodotObject.IsInstanceValid(_loadingPopup) && _loadingPopup!.IsInsideTree())
            {
                _loadingPopup.QueueFree();
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            Log.Debug("[SeedRestart] Native loading popup was already released: " + e.Message);
        }
        ClearNativeReferences();
    }

    private void ClearNativeReferences()
    {
        _loadingLayer = null;
        _loadingPopup = null;
        _loadingPanel = null;
        _quoteLabel = null;
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

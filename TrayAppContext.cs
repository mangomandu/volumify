namespace SpotifyLinearVolume;

/// <summary>
/// Tray application: owns the shared <see cref="VolumeModel"/>, the NotifyIcon, the overlay
/// and the control-panel window, keeping them all in sync.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    // We drive Spotify's OWN volume slider to position^p, and Spotify's own top-heavy curve is applied
    // on top. Lower p flattens the response (volume rises more evenly across the slider → "완만/Gentle");
    // p=1 is a linear passthrough; p→2.0 leans into Spotify's stock top-heavy feel ("가파름 → 스포티파이 기본").
    private readonly Preset[] _presets =
    {
        new("완만", "Gentle", 0.3f),
        new("살짝 완만", "Soft", 0.5f),
        new("리니어", "Linear", 1.0f),
        new("가파름", "Steep", 1.5f),
        new("스포티파이 기본", "Spotify default", 2.0f),
    };

    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly NotifyIcon _tray;
    private readonly VolumeModel _model;
    private readonly Icon? _appIcon = LoadAppIcon();
    private readonly ControlPanelForm _panel;
    private readonly OverlayBarForm _overlay;

    private ToolStripMenuItem _volLabel = null!;
    private ToolStripMenuItem _dockItem = null!;
    private ToolStripMenuItem _startupItem = null!;
    private ToolStripMenuItem _overlayItem = null!;
    private readonly List<ToolStripMenuItem> _presetItems = new();
    private readonly List<ToolStripMenuItem> _popupItems = new(); // "좁을 때 팝업" toggle mirrored in both menus
    private readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = 200 }; // poll Spotify for external volume changes

    public TrayAppContext()
    {
        Loc.Lang = Loc.FromSetting(_settings.Language);

        // Normalize mutually-exclusive modes so the menu never lies about state.
        if (_settings.OverlayOnVolume && _settings.DockToSpotify)
        {
            _settings.DockToSpotify = false;
            SettingsStore.Save(_settings);
        }

        _model = new VolumeModel(_settings.P);
        _panel = new ControlPanelForm(_model, _presets);
        if (_settings.HasDockOffset)
            _panel.SetDockOffset(new Point(_settings.DockOffsetX, _settings.DockOffsetY));
        _panel.DockOffsetChanged += offset =>
        {
            _settings.HasDockOffset = true;
            _settings.DockOffsetX = offset.X;
            _settings.DockOffsetY = offset.Y;
            SettingsStore.Save(_settings);
        };
        _panel.ApplyClientSize(new Size(_settings.PanelWidth, _settings.PanelHeight));
        _panel.PanelBoundsChanged += size =>
        {
            _settings.PanelWidth = size.Width;
            _settings.PanelHeight = size.Height;
            SettingsStore.Save(_settings);
        };
        _panel.SetDockMode(_settings.DockToSpotify);
        _overlay = new OverlayBarForm(_model);
        _overlay.SetContextMenu(BuildOverlayMenu());
        _overlay.SetPopupEnabled(_settings.OverlayPopup);

        _tray = new NotifyIcon
        {
            Icon = _appIcon ?? SystemIcons.Application,
            Text = "Volumify",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePanel();
        };

        _model.Changed += OnModelChanged;
        RefreshTrayUi();
        _overlay.SetActive(_settings.OverlayOnVolume);

        // Pull external Spotify volume changes (its own slider, hotkeys, the phone) into every surface.
        _syncTimer.Tick += (_, _) => _model.PumpExternal();
        _syncTimer.Start();
    }

    private void TogglePanel()
    {
        if (_panel.Visible) _panel.Hide();
        else _panel.ShowNearTray();
    }

    private void ToggleDock()
    {
        bool enable = !_settings.DockToSpotify;
        if (enable && _settings.OverlayOnVolume) // disable the conflicting mode first
        {
            _settings.OverlayOnVolume = false;
            _overlayItem.Checked = false;
            _overlay.SetActive(false);
        }
        _settings.DockToSpotify = enable;
        _dockItem.Checked = enable;
        _panel.SetDockMode(enable);
        SettingsStore.Save(_settings);
    }

    private void ToggleStartup()
    {
        StartupManager.SetEnabled(!StartupManager.IsEnabled());
        _startupItem.Checked = StartupManager.IsEnabled();
    }

    private void ApplyLanguage(AppLang lang)
    {
        if (Loc.Lang == lang) return;
        Loc.Lang = lang;
        _settings.Language = Loc.ToSetting(lang);
        SettingsStore.Save(_settings);

        // Rebuild both right-click menus (plus the panel title + tray tooltip) in the new language.
        _presetItems.Clear();
        _popupItems.Clear();
        _overlay.SetContextMenu(BuildOverlayMenu());
        _tray.ContextMenuStrip = BuildMenu();
        _panel.RefreshTexts();
        RefreshTrayUi();
    }

    private void ResetDock()
    {
        _panel.ResetDockOffset();
        _settings.HasDockOffset = false;
        SettingsStore.Save(_settings);
    }

    private void ToggleOverlayPopup()
    {
        _settings.OverlayPopup = !_settings.OverlayPopup;
        foreach (var it in _popupItems) it.Checked = _settings.OverlayPopup;
        _overlay.SetPopupEnabled(_settings.OverlayPopup);
        SettingsStore.Save(_settings);
    }

    private void ToggleOverlay()
    {
        bool enable = !_settings.OverlayOnVolume;
        if (enable && _settings.DockToSpotify) // disable the conflicting mode first
        {
            _settings.DockToSpotify = false;
            _dockItem.Checked = false;
            _panel.SetDockMode(false);
        }
        _settings.OverlayOnVolume = enable;
        _overlayItem.Checked = enable;
        _overlay.SetActive(enable);
        SettingsStore.Save(_settings);
    }

    private ContextMenuStrip BuildOverlayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(Loc.T("Spotify 볼륨 오버레이", "Spotify volume overlay")) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var curve = new ToolStripMenuItem(Loc.T("곡선 세기", "Volume curve"));
        foreach (var pr in _presets)
            curve.DropDownItems.Add(new ToolStripMenuItem(pr.Label, null, (_, _) => _model.SetP(pr.P)));
        menu.Items.Add(curve);

        var popupItem = new ToolStripMenuItem(Loc.T("좁을 때 팝업 슬라이더", "Pop-out slider when narrow"), null, (_, _) => ToggleOverlayPopup())
        {
            Checked = _settings.OverlayPopup,
        };
        _popupItems.Add(popupItem);
        menu.Items.Add(popupItem);

        menu.Items.Add(new ToolStripMenuItem(Loc.T("설정 패널 열기", "Open settings panel"), null, (_, _) => _panel.ShowNearTray()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Loc.T("오버레이 끄기", "Turn off overlay"), null, (_, _) => { if (_settings.OverlayOnVolume) ToggleOverlay(); }));
        return menu;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _volLabel = new ToolStripMenuItem(Loc.T("볼륨: --", "Volume: --")) { Enabled = false };
        menu.Items.Add(_volLabel);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem(Loc.T("볼륨 슬라이더 열기", "Open volume slider"), null, (_, _) => _panel.ShowNearTray()));

        var curve = new ToolStripMenuItem(Loc.T("곡선 세기", "Volume curve"));
        foreach (var pr in _presets)
        {
            var item = new ToolStripMenuItem(pr.Label, null, (_, _) => _model.SetP(pr.P));
            _presetItems.Add(item);
            curve.DropDownItems.Add(item);
        }
        menu.Items.Add(curve);

        menu.Items.Add(new ToolStripSeparator());

        _dockItem = new ToolStripMenuItem(Loc.T("Spotify 창에 붙이기", "Dock to Spotify window"), null, (_, _) => ToggleDock())
        {
            Checked = _settings.DockToSpotify,
        };
        menu.Items.Add(_dockItem);
        menu.Items.Add(new ToolStripMenuItem(Loc.T("   └ 붙는 위치 기본값으로", "   └ Reset dock position"), null, (_, _) => ResetDock()));

        _overlayItem = new ToolStripMenuItem(Loc.T("Spotify 볼륨 슬라이더에 겹치기", "Overlay on Spotify's volume slider"), null, (_, _) => ToggleOverlay())
        {
            Checked = _settings.OverlayOnVolume,
        };
        menu.Items.Add(_overlayItem);

        var overlayPopupItem = new ToolStripMenuItem(Loc.T("   └ 좁을 때 팝업 슬라이더", "   └ Pop-out slider when narrow"), null, (_, _) => ToggleOverlayPopup())
        {
            Checked = _settings.OverlayPopup,
        };
        _popupItems.Add(overlayPopupItem);
        menu.Items.Add(overlayPopupItem);

        _startupItem = new ToolStripMenuItem(Loc.T("Windows 시작 시 자동 실행", "Run at Windows startup"), null, (_, _) => ToggleStartup())
        {
            Checked = StartupManager.IsEnabled(),
        };
        menu.Items.Add(_startupItem);

        var langMenu = new ToolStripMenuItem(Loc.T("언어", "Language"));
        langMenu.DropDownItems.Add(new ToolStripMenuItem("한국어", null, (_, _) => ApplyLanguage(AppLang.Korean)) { Checked = Loc.Lang == AppLang.Korean });
        langMenu.DropDownItems.Add(new ToolStripMenuItem("English", null, (_, _) => ApplyLanguage(AppLang.English)) { Checked = Loc.Lang == AppLang.English });
        menu.Items.Add(langMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Loc.T("ℹ Spotify 볼륨을 직접 조절합니다 (폰·기기 동기화)", "ℹ Controls Spotify's own volume (syncs to phone & devices)")) { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(Loc.T("종료", "Exit"), null, (_, _) => Exit()));

        return menu;
    }

    private void OnModelChanged()
    {
        if (Math.Abs(_model.P - _settings.P) > 0.0001f)
        {
            _settings.P = _model.P;
            SettingsStore.Save(_settings);
        }

        RefreshTrayUi();
        // Feedback lives in the tray tooltip, the overlay/dock bar, and the hover popup's % —
        // no center-screen OSD flash on volume changes.
    }

    private void RefreshTrayUi()
    {
        _volLabel.Text = Loc.T("볼륨", "Volume") + $": {_model.Position * 100:0}%  (gain {_model.Gain * 100:0}%)";
        _tray.Text = $"Volumify — {_model.Position * 100:0}%";
        for (int i = 0; i < _presetItems.Count; i++)
            _presetItems[i].Checked = Math.Abs(_model.P - _presets[i].P) < 0.001f;
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            var asm = typeof(TrayAppContext).Assembly;
            string? name = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith("appicon.ico", StringComparison.OrdinalIgnoreCase));
            if (name != null)
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s != null) return new Icon(s, SystemInformation.SmallIconSize);
            }
        }
        catch { /* fall back to the system icon */ }
        return null;
    }

    private void Exit()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _syncTimer.Dispose();
            _tray.Dispose();
            _appIcon?.Dispose();
            _model.Dispose();
            _panel.Dispose();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}

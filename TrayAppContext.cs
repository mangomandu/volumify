namespace SpotifyLinearVolume;

/// <summary>
/// Tray application: owns the shared <see cref="VolumeModel"/>, the NotifyIcon, global
/// hotkeys, the OSD and the control-panel window, keeping them all in sync.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private const uint VK_UP = 0x26;
    private const uint VK_DOWN = 0x28;
    private const float Step = 0.05f;

    // gain = position ^ p.  p<1 = loud/responsive low end, gentle top.
    // p=1 = linear (proportional).  p>1 = quiet low end, fine low-volume control.
    private readonly (string Label, float P)[] _presets =
    {
        ("강하게 (0.35)", 0.35f),
        ("균형 (0.5)", 0.5f),
        ("약하게 (0.7)", 0.7f),
        ("리니어 (1.0)", 1.0f),
        ("스포티파이에 가깝게 (1.5)", 1.5f),
        ("스포티파이 기본 (2.0)", 2.0f),
    };

    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly NotifyIcon _tray;
    private readonly VolumeModel _model;
    private readonly HotkeyManager _hotkeys = new();
    private readonly Icon? _appIcon = LoadAppIcon();
    private readonly ControlPanelForm _panel;
    private readonly OverlayBarForm _overlay;

    private ToolStripMenuItem _volLabel = null!;
    private ToolStripMenuItem _dockItem = null!;
    private ToolStripMenuItem _startupItem = null!;
    private ToolStripMenuItem _overlayItem = null!;
    private readonly List<ToolStripMenuItem> _presetItems = new();
    private readonly List<ToolStripMenuItem> _popupItems = new(); // "좁을 때 팝업" toggle mirrored in both menus

    public TrayAppContext()
    {
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
            Text = "Spotify Linear Volume",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePanel();
        };

        bool upOk = _hotkeys.Register(VK_UP, () => _model.Nudge(+Step));
        bool downOk = _hotkeys.Register(VK_DOWN, () => _model.Nudge(-Step));
        if (!upOk || !downOk)
            _tray.ShowBalloonTip(3000, "Spotify Linear Volume",
                "전역 핫키(Ctrl+Alt+↑/↓) 등록 실패 — 다른 앱이 점유 중일 수 있어요. 트레이/슬라이더로 조절하세요.",
                ToolTipIcon.Warning);

        _model.Changed += OnModelChanged;
        RefreshTrayUi();
        _overlay.SetActive(_settings.OverlayOnVolume);
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
        menu.Items.Add(new ToolStripMenuItem("Spotify 볼륨 오버레이") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var curve = new ToolStripMenuItem("곡선 세기");
        foreach (var (label, p) in _presets)
            curve.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => _model.SetP(p)));
        menu.Items.Add(curve);

        var popupItem = new ToolStripMenuItem("좁을 때 팝업 슬라이더", null, (_, _) => ToggleOverlayPopup())
        {
            Checked = _settings.OverlayPopup,
        };
        _popupItems.Add(popupItem);
        menu.Items.Add(popupItem);

        menu.Items.Add(new ToolStripMenuItem("설정 패널 열기", null, (_, _) => _panel.ShowNearTray()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("오버레이 끄기", null, (_, _) => { if (_settings.OverlayOnVolume) ToggleOverlay(); }));
        return menu;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        _volLabel = new ToolStripMenuItem("볼륨: --") { Enabled = false };
        menu.Items.Add(_volLabel);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("볼륨 슬라이더 열기", null, (_, _) => _panel.ShowNearTray()));

        var curve = new ToolStripMenuItem("곡선 세기");
        foreach (var (label, p) in _presets)
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => _model.SetP(p));
            _presetItems.Add(item);
            curve.DropDownItems.Add(item);
        }
        menu.Items.Add(curve);

        menu.Items.Add(new ToolStripMenuItem("↑ 볼륨 (Ctrl+Alt+↑)", null, (_, _) => _model.Nudge(+Step)));
        menu.Items.Add(new ToolStripMenuItem("↓ 볼륨 (Ctrl+Alt+↓)", null, (_, _) => _model.Nudge(-Step)));
        menu.Items.Add(new ToolStripSeparator());

        _dockItem = new ToolStripMenuItem("Spotify 창에 붙이기", null, (_, _) => ToggleDock())
        {
            Checked = _settings.DockToSpotify,
        };
        menu.Items.Add(_dockItem);
        menu.Items.Add(new ToolStripMenuItem("   └ 붙는 위치 기본값으로", null, (_, _) => ResetDock()));

        _overlayItem = new ToolStripMenuItem("Spotify 볼륨 슬라이더에 겹치기", null, (_, _) => ToggleOverlay())
        {
            Checked = _settings.OverlayOnVolume,
        };
        menu.Items.Add(_overlayItem);

        var overlayPopupItem = new ToolStripMenuItem("   └ 좁을 때 팝업 슬라이더", null, (_, _) => ToggleOverlayPopup())
        {
            Checked = _settings.OverlayPopup,
        };
        _popupItems.Add(overlayPopupItem);
        menu.Items.Add(overlayPopupItem);

        _startupItem = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, (_, _) => ToggleStartup())
        {
            Checked = StartupManager.IsEnabled(),
        };
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("ℹ Spotify 자체 볼륨은 100%로 두세요") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem("종료", null, (_, _) => Exit()));

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
        _volLabel.Text = $"볼륨: {_model.Position * 100:0}%  (gain {_model.Gain * 100:0}%)";
        _tray.Text = $"Spotify Linear Volume — {_model.Position * 100:0}%";
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
            _tray.Dispose();
            _appIcon?.Dispose();
            _hotkeys.Dispose();
            _model.Dispose();
            _panel.Dispose();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}

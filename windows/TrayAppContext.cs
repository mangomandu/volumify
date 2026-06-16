namespace Volumify;

/// <summary>
/// Tray application: owns the shared <see cref="VolumeModel"/>, the NotifyIcon, the overlay
/// and the control-panel window, keeping them all in sync.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    // We set Spotify's OWN slider value to position^p; Spotify then applies its own steep, top-heavy
    // curve on top (≈ value⁴, confirmed by community reports + measurement). So the perceived loudness
    // ≈ position^(2.4·p): p<1 flattens it toward an even, usable slider (p≈0.4 ≈ loudness tracks the
    // slider position); p=1 leaves Spotify's raw top-heavy default. p>1 only makes it worse, so we
    // don't offer it.
    private readonly Preset[] _presets =
    {
        new("리니어", "Linear", 0.3f),
        new("고름", "Even", 0.4f),
        new("살짝 쏠림", "Slight ramp", 0.6f),
        new("스포티파이 디폴트", "Spotify default", 1.0f),
    };

    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly NotifyIcon _tray;
    private readonly VolumeModel _model;
    private readonly Icon? _appIcon = LoadAppIcon();
    private readonly ControlPanelForm _panel;
    private readonly OverlayBarForm _overlay;
    private readonly NowPlaying _nowPlaying = new();
    private readonly LyricsForm _lyricsForm;
    private readonly SpotifyAuth _spotifyAuth;

    private ToolStripMenuItem _volLabel = null!;
    private ToolStripMenuItem _dockItem = null!;
    private ToolStripMenuItem _startupItem = null!;
    private ToolStripMenuItem _overlayItem = null!;
    private ToolStripMenuItem _lyricsItem = null!;
    private ToolStripMenuItem _keepItem = null!;
    private ToolStripMenuItem _loginItem = null!;
    private readonly List<ToolStripMenuItem> _presetItems = new();
    private readonly List<ToolStripMenuItem> _popupItems = new(); // "좁을 때 팝업" toggle mirrored in both menus
    private readonly System.Windows.Forms.Timer _syncTimer = new() { Interval = 200 }; // poll Spotify for external volume changes

    public TrayAppContext()
    {
        Loc.Lang = Loc.FromSetting(_settings.Language);
        if (_settings.AccentArgb != 0) Theme.SetAccent(Color.FromArgb(_settings.AccentArgb)); // custom accent before any window paints

        _spotifyAuth = new SpotifyAuth(_settings.SpotifyClientId, _settings.SpotifyRefreshToken);
        _spotifyAuth.RefreshTokenChanged += () =>
        {
            _settings.SpotifyRefreshToken = _spotifyAuth.RefreshToken;
            _settings.SpotifyClientId = _spotifyAuth.ClientId;
            SettingsStore.Save(_settings);
        };

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

        _lyricsForm = new LyricsForm(_nowPlaying);
        RestoreLyricsBounds();
        _lyricsForm.CloseRequested += () => { if (_settings.LyricsEnabled) ToggleLyrics(); };
        _lyricsForm.BoundsChanged += b =>
        {
            _settings.HasLyricsBounds = true;
            _settings.LyricsX = b.X; _settings.LyricsY = b.Y; _settings.LyricsW = b.Width; _settings.LyricsH = b.Height;
            SettingsStore.Save(_settings);
        };
        _lyricsForm.DockOffsetChanged += off =>
        {
            _settings.HasLyricsDockOffset = true;
            _settings.LyricsDockOffsetX = off.X; _settings.LyricsDockOffsetY = off.Y;
            SettingsStore.Save(_settings);
        };
        if (_settings.HasLyricsDockOffset)
            _lyricsForm.SetDockOffset(new Point(_settings.LyricsDockOffsetX, _settings.LyricsDockOffsetY));
        _lyricsForm.SetKeepWhenMinimized(_settings.LyricsKeepWhenMinimized);
        _lyricsForm.TrackIdProvider = (title, dur, ct) => _spotifyAuth.IsLinked ? _spotifyAuth.GetCurrentTrackIdAsync(title, dur, ct) : Task.FromResult<string?>(null);
        _lyricsForm.NextTrackProvider = async ct =>
        {
            if (!_spotifyAuth.IsLinked) return null;
            var (id, title, artist, dur) = await _spotifyAuth.GetNextTrackAsync(ct);
            return id == null ? null : new NowPlaying.TrackInfo(artist, title, "", dur, id);
        };

        // Repaint every surface live when the accent color changes (the popup picks it up on its next show).
        Theme.AccentChanged += () => { _panel.Invalidate(true); _overlay.Invalidate(true); _lyricsForm.Invalidate(); };

        LyricsProvider.CleanCache(); // drop superseded cache versions + cap size (background, best-effort)

        // Reuse a saved Musixmatch token (token.get is rate-limited); persist a freshly minted one.
        LyricsProvider.InitToken(_settings.MusixmatchToken, tok =>
        {
            _settings.MusixmatchToken = tok;
            SettingsStore.Save(_settings);
        });
        if (_settings.LyricsEnabled) _lyricsForm.SetActive(true);

        // Pull external Spotify volume changes (its own slider, hotkeys, the phone) into every surface.
        _syncTimer.Tick += (_, _) => _model.PumpExternal();
        _syncTimer.Start();
    }

    private void TogglePanel()
    {
        if (_panel.Visible) _panel.HideByUser();
        else _panel.RequestShow();
    }

    private void ToggleDock()
    {
        // Docking the control panel and the overlay bar are independent — both can be on at once.
        bool enable = !_settings.DockToSpotify;
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
        // The overlay bar and panel docking are independent — leave dock mode untouched.
        bool enable = !_settings.OverlayOnVolume;
        _settings.OverlayOnVolume = enable;
        _overlayItem.Checked = enable;
        _overlay.SetActive(enable);
        SettingsStore.Save(_settings);
    }

    private void ToggleLyrics()
    {
        bool enable = !_settings.LyricsEnabled;
        _settings.LyricsEnabled = enable;
        _lyricsItem.Checked = enable;
        _lyricsForm.SetActive(enable);
        SettingsStore.Save(_settings);
    }

    private void RestoreLyricsBounds()
    {
        if (_settings.HasLyricsBounds
            && _settings.LyricsW >= _lyricsForm.MinimumSize.Width && _settings.LyricsH >= _lyricsForm.MinimumSize.Height)
        {
            var rect = new Rectangle(_settings.LyricsX, _settings.LyricsY, _settings.LyricsW, _settings.LyricsH);
            foreach (var s in Screen.AllScreens)
                if (s.WorkingArea.IntersectsWith(rect)) { _lyricsForm.Bounds = rect; return; }
        }
        var wa = (Screen.PrimaryScreen ?? Screen.FromControl(_lyricsForm)).WorkingArea;
        _lyricsForm.Location = new Point(wa.Right - _lyricsForm.Width - 48, wa.Top + 96);
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

        menu.Items.Add(new ToolStripMenuItem(Loc.T("설정 패널 열기", "Open settings panel"), null, (_, _) => _panel.RequestShow()));
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

        // --- primary controls ---
        var curve = new ToolStripMenuItem(Loc.T("곡선 세기", "Volume curve"));
        foreach (var pr in _presets)
        {
            var item = new ToolStripMenuItem(pr.Label, null, (_, _) => _model.SetP(pr.P));
            _presetItems.Add(item);
            curve.DropDownItems.Add(item);
        }
        menu.Items.Add(curve);

        _lyricsItem = new ToolStripMenuItem(Loc.T("가사 창", "Lyrics window"), null, (_, _) => ToggleLyrics()) { Checked = _settings.LyricsEnabled };
        menu.Items.Add(_lyricsItem);

        _overlayItem = new ToolStripMenuItem(Loc.T("Spotify 볼륨에 겹치기", "Overlay Spotify's volume"), null, (_, _) => ToggleOverlay()) { Checked = _settings.OverlayOnVolume };
        menu.Items.Add(_overlayItem);

        menu.Items.Add(new ToolStripMenuItem(Loc.T("볼륨 슬라이더 열기", "Open volume slider"), null, (_, _) => _panel.RequestShow()));

        menu.Items.Add(new ToolStripSeparator());

        // --- everything else tucked into Settings ---
        var settings = new ToolStripMenuItem(Loc.T("설정", "Settings"));

        _dockItem = new ToolStripMenuItem(Loc.T("패널을 Spotify 창에 붙이기", "Dock panel to Spotify"), null, (_, _) => ToggleDock()) { Checked = _settings.DockToSpotify };
        settings.DropDownItems.Add(_dockItem);
        settings.DropDownItems.Add(new ToolStripMenuItem(Loc.T("   └ 붙는 위치 기본값으로", "   └ Reset dock position"), null, (_, _) => ResetDock()));

        var overlayPopupItem = new ToolStripMenuItem(Loc.T("오버레이 좁을 때 팝업", "Pop-out slider when narrow"), null, (_, _) => ToggleOverlayPopup()) { Checked = _settings.OverlayPopup };
        _popupItems.Add(overlayPopupItem);
        settings.DropDownItems.Add(overlayPopupItem);

        _keepItem = new ToolStripMenuItem(Loc.T("최소화해도 가사 유지", "Keep lyrics when Spotify is minimized"), null, (_, _) => ToggleLyricsKeep()) { Checked = _settings.LyricsKeepWhenMinimized };
        settings.DropDownItems.Add(_keepItem);

        settings.DropDownItems.Add(new ToolStripSeparator());

        var accentMenu = new ToolStripMenuItem(Loc.T("강조색", "Accent color"));
        accentMenu.DropDownItems.Add(new ToolStripMenuItem(Loc.T("스포티파이 그린 (기본)", "Spotify green (default)"), null, (_, _) => SetAccentPreset(Theme.DefaultAccent)));
        accentMenu.DropDownItems.Add(new ToolStripMenuItem(Loc.T("코랄", "Coral"), null, (_, _) => SetAccentPreset(Color.FromArgb(204, 120, 92))));
        accentMenu.DropDownItems.Add(new ToolStripMenuItem(Loc.T("직접 선택… (색상코드 / 마우스)", "Custom… (hex / picker)"), null, (_, _) => PickAccent()));
        settings.DropDownItems.Add(accentMenu);

        _loginItem = new ToolStripMenuItem(LoginLabel(), null, (_, _) => SpotifyLogin());
        settings.DropDownItems.Add(_loginItem);

        _startupItem = new ToolStripMenuItem(Loc.T("Windows 시작 시 자동 실행", "Run at Windows startup"), null, (_, _) => ToggleStartup()) { Checked = StartupManager.IsEnabled() };
        settings.DropDownItems.Add(_startupItem);

        var langMenu = new ToolStripMenuItem(Loc.T("언어", "Language"));
        langMenu.DropDownItems.Add(new ToolStripMenuItem("한국어", null, (_, _) => ApplyLanguage(AppLang.Korean)) { Checked = Loc.Lang == AppLang.Korean });
        langMenu.DropDownItems.Add(new ToolStripMenuItem("English", null, (_, _) => ApplyLanguage(AppLang.English)) { Checked = Loc.Lang == AppLang.English });
        settings.DropDownItems.Add(langMenu);

        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Loc.T("종료", "Exit"), null, (_, _) => Exit()));

        return menu;
    }

    private void ToggleLyricsKeep()
    {
        _settings.LyricsKeepWhenMinimized = !_settings.LyricsKeepWhenMinimized;
        _keepItem.Checked = _settings.LyricsKeepWhenMinimized;
        _lyricsForm.SetKeepWhenMinimized(_settings.LyricsKeepWhenMinimized);
        SettingsStore.Save(_settings);
    }

    private void PickAccent()
    {
        using var dlg = new ColorDialog { Color = Theme.Accent, FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        SetAccentPreset(dlg.Color);
    }

    private void SetAccentPreset(Color c)
    {
        Theme.SetAccent(c);
        _settings.AccentArgb = Theme.Accent.ToArgb();
        SettingsStore.Save(_settings);
    }

    private string LoginLabel() => _spotifyAuth.IsLinked
        ? Loc.T("스포티파이 연결됨 ✓ (정확한 가사)", "Spotify linked ✓ (exact lyrics)")
        : Loc.T("스포티파이 로그인 (정확한 가사)", "Log in to Spotify (exact lyrics)");

    private void SpotifyLogin()
    {
        if (_spotifyAuth.IsLinked)
        {
            var r = MessageBox.Show(
                Loc.T("이미 연결됨.\n\n예 = 다시 로그인 (권한 갱신)\n아니오 = 연결 해제", "Already linked.\n\nYes = re-login (refresh permissions)\nNo = unlink"),
                "Volumify", MessageBoxButtons.YesNoCancel);
            if (r == DialogResult.No) { _spotifyAuth.Unlink(); _loginItem.Text = LoginLabel(); }
            else if (r == DialogResult.Yes) _ = DoLoginAsync(); // re-authorize with the current scopes (saved client id)
            return;
        }
        using var dlg = new SpotifyLoginDialog(_spotifyAuth.ClientId);
        if (dlg.ShowDialog() != DialogResult.OK || dlg.ClientId.Length == 0) return;
        _spotifyAuth.SetClientId(dlg.ClientId);
        _settings.SpotifyClientId = dlg.ClientId;
        SettingsStore.Save(_settings);
        _ = DoLoginAsync();
    }

    private async Task DoLoginAsync()
    {
        bool ok = false;
        try { ok = await _spotifyAuth.LoginAsync(CancellationToken.None); } catch { }
        _loginItem.Text = LoginLabel();
        MessageBox.Show(
            ok ? Loc.T("연결 완료! 이제 스포티파이랑 똑같은 가사가 나와요.", "Linked! Lyrics now match Spotify exactly.")
               : Loc.T("로그인 실패 — Client ID와 Redirect URI를 확인해주세요.", "Login failed — check the Client ID and Redirect URI."),
            "Volumify");
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
            _lyricsForm.Dispose();
            _nowPlaying.Dispose();
        }
        base.Dispose(disposing);
    }
}

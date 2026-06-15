namespace Volumify;

/// <summary>Collects the user's Spotify app Client ID for the read-only "currently playing" login.</summary>
public sealed class SpotifyLoginDialog : Form
{
    private readonly TextBox _idBox = new();
    public string ClientId => _idBox.Text.Trim();

    public SpotifyLoginDialog(string currentClientId)
    {
        Text = Loc.T("스포티파이 로그인 설정", "Spotify login setup");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        ClientSize = new Size(486, 296);
        BackColor = Color.FromArgb(28, 27, 25);
        ForeColor = Color.FromArgb(235, 232, 227);
        Font = new Font("Segoe UI", 9f);

        Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(16, 14),
            Size = new Size(454, 118),
            Text = Loc.T(
                "정확한 가사를 위해 본인 스포티파이 앱을 한 번 만들면 돼요:\n\n" +
                "1) 아래 '개발자 대시보드 열기' → 로그인 → Create app\n" +
                "2) Redirect URI 에 아래 주소를 그대로 추가하고 저장\n" +
                "3) API 항목에서 'Web API' 체크\n" +
                "4) Client ID 를 복사해 아래에 붙여넣고 '로그인'",
                "To get exact lyrics, create your own Spotify app once:\n\n" +
                "1) Open the developer dashboard below → log in → Create app\n" +
                "2) Add the Redirect URI below exactly, then save\n" +
                "3) Tick 'Web API'\n" +
                "4) Copy the Client ID, paste it below, and press Log in"),
        });

        var dash = new LinkLabel
        {
            Text = Loc.T("↗ 개발자 대시보드 열기", "↗ Open developer dashboard"),
            Location = new Point(16, 138), AutoSize = true,
            LinkColor = Color.FromArgb(30, 215, 96), ActiveLinkColor = Color.White,
        };
        dash.LinkClicked += (_, _) => Open("https://developer.spotify.com/dashboard");
        Controls.Add(dash);

        Controls.Add(new Label { Text = "Redirect URI", Location = new Point(16, 170), AutoSize = true, ForeColor = Color.FromArgb(150, 145, 138) });
        var redir = new TextBox
        {
            Text = SpotifyAuth.RedirectUri, Location = new Point(112, 167), Size = new Size(358, 22),
            ReadOnly = true, BackColor = Color.FromArgb(42, 40, 37), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
        };
        redir.Enter += (_, _) => redir.SelectAll();
        Controls.Add(redir);

        Controls.Add(new Label { Text = "Client ID", Location = new Point(16, 206), AutoSize = true, ForeColor = Color.FromArgb(150, 145, 138) });
        _idBox.Text = currentClientId;
        _idBox.Location = new Point(112, 203); _idBox.Size = new Size(358, 22);
        _idBox.BackColor = Color.FromArgb(42, 40, 37); _idBox.ForeColor = Color.White; _idBox.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_idBox);

        var ok = new Button
        {
            Text = Loc.T("로그인", "Log in"), DialogResult = DialogResult.OK,
            Location = new Point(290, 250), Size = new Size(88, 30), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 215, 96), ForeColor = Color.Black,
        };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button
        {
            Text = Loc.T("취소", "Cancel"), DialogResult = DialogResult.Cancel,
            Location = new Point(384, 250), Size = new Size(86, 30), FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(80, 78, 74);
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }

    private static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}

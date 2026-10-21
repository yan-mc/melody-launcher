using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MelodyLauncher.Services;

namespace MelodyLauncher.Views
{
    public partial class AccountOverlay : UserControl
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
        public event EventHandler CloseRequested;
        public event EventHandler AccountChanged;

        private CancellationTokenSource? _loginCts;
        private bool _busy;

        public AccountOverlay()
        {
            InitializeComponent();
            try { var __mc = MelodyLauncher.Services.AppSettings.Load().McFolder;  } catch { }
            
            BtnClose.Click += (s, e) => { CancelLogin(); CloseRequested?.Invoke(this, EventArgs.Empty); };
            BtnLogin.Click += async (s, e) => await StartLoginAsync();
            BtnLogout.Click += (s, e) => Logout();
            BtnSaveClient.Click += (s, e) => SaveClientId();
            Refresh();
        }

        public void Refresh()
        {
            var s = AppSettings.Load();
            EditClientId.Text = string.IsNullOrEmpty(s.MsClientId) ? "" : s.MsClientId;
            if (!string.IsNullOrEmpty(s.MsRefreshToken) && !string.IsNullOrEmpty(s.MsUserName))
            {
                TextState.Text = "已登录：微软正版账户";
                TextName.Text = "角色名：" + s.MsUserName;
                TextUuid.Text = "UUID：" + s.MsUuid;
                BtnLogin.Content = "重新登录";
                BtnLogout.IsEnabled = true;
            }
            else
            {
                TextState.Text = "未登录（当前使用离线登录）";
                TextName.Text = "角色名：-";
                TextUuid.Text = "UUID：-";
                BtnLogin.Content = "微软登录";
                BtnLogout.IsEnabled = false;
            }
        }

        private void SaveClientId()
        {
            var s = AppSettings.Load();
            s.MsClientId = EditClientId.Text.Trim();
            s.Save();
            SetStatus("Client ID 已保存");
        }

        private void Logout()
        {
            var s = AppSettings.Load();
            s.MsRefreshToken = "";
            s.MsUserName = "";
            s.MsUuid = "";
            s.Save();
            SetStatus("已退出微软账户");
            Refresh();
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CancelLogin()
        {
            try { _loginCts?.Cancel(); } catch { }
            _loginCts = null;
        }

        private void SetStatus(string text, bool error = false)
        {
            TextStatus.Text = text;
            TextStatus.Foreground = new SolidColorBrush(error
                ? Color.FromRgb(0x8B, 0x1A, 0x1A)
                : Color.FromRgb(0x17, 0x3C, 0x44));
        }

        private async Task StartLoginAsync()
        {
            if (_busy) return;
            var s = AppSettings.Load();
            var clientId = string.IsNullOrWhiteSpace(EditClientId.Text) ? s.MsClientId : EditClientId.Text.Trim();
            if (string.IsNullOrWhiteSpace(clientId)) { SetStatus("请先填写 OAuth Client ID", true); return; }
            _busy = true;
            BtnLogin.IsEnabled = false;
            _loginCts = new CancellationTokenSource();
            var ct = _loginCts.Token;
            try
            {
                SetStatus("正在申请设备代码…");
                var form = new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["tenant"] = "/consumers",
                    ["scope"] = "XboxLive.signin offline_access"
                };
                var resp = await Http.PostAsync(
                    "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
                    new FormUrlEncodedContent(form), ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                {
                    SetStatus("申请失败：HTTP " + (int)resp.StatusCode + "（Client ID 可能不可用）", true);
                    return;
                }
                using var doc = JsonDocument.Parse(body);
                var deviceCode = doc.RootElement.GetProperty("device_code").GetString() ?? "";
                var userCode = doc.RootElement.GetProperty("user_code").GetString() ?? "";
                var verifyUri = doc.RootElement.TryGetProperty("verification_uri", out var vu) ? vu.GetString() ?? "" : "";
                int interval = doc.RootElement.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5;
                int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 900;

                TextCode.Text = userCode;
                try { Clipboard.SetText(userCode); } catch { }
                SetStatus("已复制代码，正在打开登录网页…");
                if (!string.IsNullOrEmpty(verifyUri))
                {
                    try { Process.Start(new ProcessStartInfo(verifyUri) { UseShellExecute = true }); } catch { }
                }

                var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, expiresIn - 5));
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, interval)), ct);
                    SetStatus("等待网页完成登录…（代码已复制）");
                    var pollForm = new Dictionary<string, string>
                    {
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                        ["client_id"] = clientId,
                        ["device_code"] = deviceCode,
                        ["scope"] = "XboxLive.signin offline_access"
                    };
                    var pollResp = await Http.PostAsync(
                        "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                        new FormUrlEncodedContent(pollForm), ct);
                    var pollBody = await pollResp.Content.ReadAsStringAsync(ct);
                    if (pollResp.IsSuccessStatusCode)
                    {
                        using var pd = JsonDocument.Parse(pollBody);
                        var refresh = pd.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
                        var access = pd.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(refresh)) { SetStatus("登录失败：未拿到 refresh_token", true); return; }
                        SetStatus("授权成功，正在获取 Minecraft 角色…");
                        await CompleteLoginAsync(clientId, refresh, access, ct);
                        return;
                    }
                    // authorization_pending / slow_down → 继续轮询
                    SetStatus("等待网页完成登录…（代码已复制）");
                }
                SetStatus("登录超时，请重试", true);
            }
            catch (OperationCanceledException)
            {
                SetStatus("已取消登录");
            }
            catch (Exception ex)
            {
                SetStatus("登录异常：" + ex.Message, true);
            }
            finally
            {
                _busy = false;
                BtnLogin.IsEnabled = true;
            }
        }

        /// <summary>refresh token → Xbox → XSTS → Minecraft 角色（用于显示；引擎启动时还会再走一遍）。</summary>
        private async Task CompleteLoginAsync(string clientId, string refreshToken, string accessToken, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrEmpty(accessToken))
                    accessToken = await RefreshMsTokenAsync(clientId, refreshToken, ct);
                var (xbl, uhs) = await XboxAuthAsync(accessToken, ct);
                var xsts = await XstsAsync(xbl, ct);
                var mcToken = await McLoginAsync(xsts, uhs, ct);
                var (name, uuid) = await McProfileAsync(mcToken, ct);
                var s = AppSettings.Load();
                s.MsClientId = clientId;
                s.MsRefreshToken = refreshToken;
                s.MsUserName = name;
                s.MsUuid = uuid;
                s.Save();
                TextCode.Text = "";
                SetStatus("登录成功：" + name);
                Refresh();
                AccountChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                SetStatus("获取角色失败：" + ex.Message, true);
            }
        }

        private static async Task<string> RefreshMsTokenAsync(string clientId, string refreshToken, CancellationToken ct)
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
                ["scope"] = "XboxLive.signin offline_access"
            };
            var resp = await Http.PostAsync("https://login.live.com/oauth20_token.srf", new FormUrlEncodedContent(form), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new Exception("refresh_token 刷新失败 HTTP " + (int)resp.StatusCode);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("access_token").GetString() ?? "";
        }

        private static async Task<(string Token, string Uhs)> XboxAuthAsync(string accessToken, CancellationToken ct)
        {
            var payload = new
            {
                Properties = new { RpsTicket = "d=" + accessToken, SiteName = "user.auth.xboxlive.com" },
                RelyingParty = "http://auth.xboxlive.com",
                TokenType = "JWT"
            };
            var resp = await Http.PostAsync("https://user.auth.xboxlive.com/user/authenticate",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new Exception("Xbox 认证失败 HTTP " + (int)resp.StatusCode);
            using var doc = JsonDocument.Parse(body);
            var token = doc.RootElement.GetProperty("Token").GetString() ?? "";
            var uhs = doc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString() ?? "";
            return (token, uhs);
        }

        private static async Task<string> XstsAsync(string xblToken, CancellationToken ct)
        {
            var payload = new
            {
                Properties = new { SandboxId = "RETAIL" },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
                UserTokens = new[] { xblToken }
            };
            var resp = await Http.PostAsync("https://xsts.auth.xboxlive.com/xsts/authorize",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new Exception("XSTS 认证失败 HTTP " + (int)resp.StatusCode + "（可能未购买 MC 或地区限制）");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("Token").GetString() ?? "";
        }

        private static async Task<string> McLoginAsync(string xsts, string uhs, CancellationToken ct)
        {
            var payload = new { identityToken = "XBL3.0 x=" + uhs + ";" + xsts };
            var resp = await Http.PostAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new Exception("Minecraft 登录失败 HTTP " + (int)resp.StatusCode);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("access_token").GetString() ?? "";
        }

        private static async Task<(string Name, string Uuid)> McProfileAsync(string mcToken, CancellationToken ct)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcToken);
            var resp = await Http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new Exception("获取角色失败 HTTP " + (int)resp.StatusCode + "（该账户可能没有 Minecraft）");
            using var doc = JsonDocument.Parse(body);
            return (doc.RootElement.GetProperty("name").GetString() ?? "",
                    doc.RootElement.GetProperty("id").GetString() ?? "");
        }
    }
}

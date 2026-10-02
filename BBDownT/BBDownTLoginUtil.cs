using QRCoder;
using System;
using System.IO;
using System.Threading.Tasks;
using static BBDownT.BBDownTUtil;
using static BBDownT.Core.Logger;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using BBDownT.Core;
using BBDownT.Core.Util;

namespace BBDownT;

internal static class BBDownTLoginUtil
{
    private static async Task<LoginStatusResult> GetLoginStatusAsync(string qrcodeKey)
    {
        string queryUrl = $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrcodeKey}&source=main-fe-header";
        using var request = new HttpRequestMessage(HttpMethod.Get, queryUrl);
        HTTPUtil.ApplyWebRequestHeaders(request, queryUrl, sendCookie: false, forceAuthenticatedProfile: true);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com/");
        request.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");

        using var response = (await HTTPUtil.AppHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)).EnsureSuccessStatusCode();
        var responseBody = await response.Content.ReadAsStringAsync();
        var setCookieHeaders = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : [];
        return new LoginStatusResult(responseBody, setCookieHeaders);
    }

    public static async Task<WebLoginQrCode> CreateWebLoginQrCodeAsync()
    {
        AuthenticatedWebProfileStore.Configure(Program.APP_DIR);
        string loginUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate?source=main-fe-header";
        string url = JsonDocument.Parse(await HTTPUtil.GetAuthenticatedWebSourceAsync(loginUrl)).RootElement.GetProperty("data").GetProperty("url").ToString();
        return new WebLoginQrCode(url, GetQueryString("qrcode_key", url));
    }

    /// <summary>
    /// 查询一次扫码状态；登录成功时保存Cookie并立即在当前进程生效
    /// </summary>
    public static async Task<WebLoginState> PollWebLoginAsync(string qrcodeKey)
    {
        var loginStatus = await GetLoginStatusAsync(qrcodeKey);
        using var loginDoc = JsonDocument.Parse(loginStatus.ResponseBody);
        var loginData = loginDoc.RootElement.GetProperty("data");
        int code = loginData.GetProperty("code").GetInt32();
        switch (code)
        {
            case 86038: return WebLoginState.Expired;
            case 86101: return WebLoginState.Waiting; //等待扫码
            case 86090: return WebLoginState.Scanned; //等待确认
        }

        string cc = loginData.GetProperty("url").ToString();
        string? refreshToken = loginData.TryGetProperty("refresh_token", out var refreshTokenElement)
            ? refreshTokenElement.GetString()
            : null;
        var cookie = BBDownTCookieRefreshUtil.NormalizeLoginCookie(cc, refreshToken, loginStatus.SetCookieHeaders);
        if (!BBDownTCookieRefreshUtil.HasRequiredLoginCookies(cookie))
        {
            throw new InvalidOperationException("登录响应缺少SESSDATA或bili_jct，未覆盖现有Cookie文件。");
        }

        await File.WriteAllTextAsync(Path.Combine(Program.APP_DIR, "BBDownT.data"), cookie);
        Config.COOKIE = cookie;
        return WebLoginState.Success;
    }

    /// <summary>
    /// 返回当前WEB登录账号的昵称，未登录时返回null
    /// </summary>
    public static async Task<string?> GetWebLoginUserNameAsync()
    {
        if (string.IsNullOrEmpty(Config.COOKIE)) return null;
        var source = await HTTPUtil.GetWebSourceAsync("https://api.bilibili.com/x/web-interface/nav");
        var data = JsonDocument.Parse(source).RootElement.GetProperty("data");
        return data.GetProperty("isLogin").GetBoolean() ? data.GetProperty("uname").GetString() : null;
    }

    public static void LogoutWEB()
    {
        File.Delete(Path.Combine(Program.APP_DIR, "BBDownT.data"));
        Config.COOKIE = "";
    }

    public static async Task LoginWEB()
    {
        try
        {
            Log("获取登录地址...");
            var qrCode = await CreateWebLoginQrCodeAsync();
            bool flag = false;
            Log("生成二维码...");
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrCode.Url, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode pngByteCode = new(qrCodeData);
            await File.WriteAllBytesAsync("qrcode.png", pngByteCode.GetGraphic(7));
            Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
            var consoleQRCode = new ConsoleQRCode(qrCodeData);
            consoleQRCode.GetGraphic();

            while (true)
            {
                await Task.Delay(1000);
                var state = await PollWebLoginAsync(qrCode.QrcodeKey);
                if (state == WebLoginState.Expired)
                {
                    LogColor("二维码已过期, 请重新执行登录指令.");
                    break;
                }
                else if (state == WebLoginState.Scanned)
                {
                    if (!flag)
                    {
                        Log("扫码成功, 请确认...");
                        flag = !flag;
                    }
                }
                else if (state == WebLoginState.Success)
                {
                    Log("登录成功");
                    File.Delete("qrcode.png");
                    break;
                }
            }
        }
        catch (Exception e) { LogError(e.Message); }
    }

    public static async Task LoginTV()
    {
        try
        {
            string loginUrl = "https://passport.snm0516.aisee.tv/x/passport-tv-login/qrcode/auth_code";
            string pollUrl = "https://passport.bilibili.com/x/passport-tv-login/qrcode/poll";
            var parms = GetTVLoginParms();
            Log("获取登录地址...");
            byte[] responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(loginUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
            string web = Encoding.UTF8.GetString(responseArray);
            string url = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("url").ToString();
            string authCode = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("auth_code").ToString();
            Log("生成二维码...");
            QRCodeGenerator qrGenerator = new();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            PngByteQRCode pngByteCode = new(qrCodeData);
            await File.WriteAllBytesAsync("qrcode.png", pngByteCode.GetGraphic(7));
            Log("生成二维码成功: qrcode.png, 请打开并扫描, 或扫描打印的二维码");
            var consoleQRCode = new ConsoleQRCode(qrCodeData);
            consoleQRCode.GetGraphic();
            parms.Set("auth_code", authCode);
            parms.Set("ts", GetTimeStamp(true));
            parms.Remove("sign");
            parms.Add("sign", GetSign(ToQueryString(parms)));
            while (true)
            {
                await Task.Delay(1000);
                responseArray = await (await HTTPUtil.AppHttpClient.PostAsync(pollUrl, new FormUrlEncodedContent(parms.ToDictionary()))).Content.ReadAsByteArrayAsync();
                web = Encoding.UTF8.GetString(responseArray);
                string code = JsonDocument.Parse(web).RootElement.GetProperty("code").ToString();
                if (code == "86038")
                {
                    LogColor("二维码已过期, 请重新执行登录指令.");
                    break;
                }
                else if (code == "86039") //等待扫码
                {
                    continue;
                }
                else
                {
                    string cc = JsonDocument.Parse(web).RootElement.GetProperty("data").GetProperty("access_token").ToString();
                    Log("登录成功: AccessToken=" + cc);
                    //导出cookie
                    await File.WriteAllTextAsync(Path.Combine(Program.APP_DIR, "BBDownTTV.data"), "access_token=" + cc);
                    File.Delete("qrcode.png");
                    break;
                }
            }
        }
        catch (Exception e) { LogError(e.Message); }
    }

    private readonly record struct LoginStatusResult(string ResponseBody, string[] SetCookieHeaders);
}

internal enum WebLoginState { Waiting, Scanned, Expired, Success }

internal sealed record WebLoginQrCode(string Url, string QrcodeKey);

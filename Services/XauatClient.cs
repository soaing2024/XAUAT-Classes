using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XauatSchedule.Core;

namespace XauatSchedule.Services;

public sealed record LoginResult(bool Success, bool NeedsCaptcha, byte[]? CaptchaImage, string Message,
    bool NeedsManual = false, string ManualDetail = "", string? ManualUrl = null);

/// <summary>登录成功后从教务系统取到的"身份与课程上下文"。</summary>
public sealed record BootstrapResult(
    bool Success,
    string Message,
    int StdPersonId = 0,
    string SemesterId = "",
    int CurrentWeek = 0,
    int TimeTableLayoutId = 0,
    int[]? LessonIds = null,
    List<SlotRecord>? Slots = null,
    List<(string Id, string Name)>? Semesters = null,
    string StudentName = "",
    string AdminClass = "",
    int WeekCount = 20);

public sealed record FetchResult(bool Success, string Message, List<LessonRecord> Lessons, string FetchedAt,
    bool NeedsManual = false, string ManualDetail = "", string? ManualUrl = null);

/// <summary>
/// 西安建筑科技大学教务系统的抓取通道（内置，无需外部脚本、无需随包数据）。
///
/// 完整流程与浏览器一致：
///   1) /student/sso/login → 统一身份认证 authserver（AES-128-CBC 加密密码，POST 必须带 service）
///   2) 带 ticket 回跳教务系统，建立会话
///   3) GET /for-std/course-table  ← 服务端渲染的课表页，里面有 stdPersonId 与学期列表
///   4) GET /for-std/course-table/get-data ← 返回 lessonIds / timeTableLayoutId / currentWeek
///   5) POST /ws/schedule-table/timetable-layout ← 返回真实节次时间表
///   6) POST /ws/schedule-table/datum ← 逐周取课表
///
/// 任何人机校验（图片验证码 / 滑块 / 风控 / 频率限制）都原样上报给界面，由用户自己完成，
/// 程序不做任何绕过。
/// </summary>
public sealed class XauatClient : IDisposable
{
    private const string AppOrigin = "https://swjw.xauat.edu.cn";
    private const string AppBase = AppOrigin + "/student";
    private const string IdpOrigin = "http://authserver.xauat.edu.cn";
    private const string Service = AppBase + "/sso/login";
    private const string UA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private CookieContainer _appCookies = new();
    private CookieContainer _idpCookies = new();
    private HttpClient _app = null!;
    private HttpClient _idp = null!;

    private string _idpLoginUrl = "";
    private string _salt = "";
    private string _execution = "e1s1";
    private string _lt = "";

    public static Action<string>? Trace;
    public List<string> RawSetCookies { get; } = new();

    public XauatClient() => BuildClients();

    /// <summary>每次登录都从干净会话开始：复用半成品会话会被教务系统跳到别处。</summary>
    private void BuildClients()
    {
        _app?.Dispose();
        _idp?.Dispose();
        _appCookies = new CookieContainer();
        _idpCookies = new CookieContainer();
        _app = BuildClient(_appCookies);
        _idp = BuildClient(_idpCookies);
    }

    private static HttpClient BuildClient(CookieContainer jar)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = jar,
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UA);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        return client;
    }

    private static string MakeAbsolute(string origin, string location)
        => location.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? location
            : new Uri(new Uri(origin), location).ToString();

    private static string Cut(string? s, int n = 90)
        => string.IsNullOrEmpty(s) ? "-" : s.Length <= n ? s : s[..n] + "…";

    // ── 登录 ────────────────────────────────────────────────

    public async Task<LoginResult> LoginAsync(string username, string password, string? captcha = null,
        CancellationToken ct = default)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                BuildClients();
                Trace?.Invoke($"1) 请求教务系统 SSO 入口（第 {attempt + 1} 次，全新会话）");
                var first = await SendAsync(_app, new HttpRequestMessage(HttpMethod.Get, Service), ct);
                var location = first.Headers.Location?.ToString();
                Trace?.Invoke($"   返回 {(int)first.StatusCode} location={Cut(location)}");

                if (string.IsNullOrEmpty(location))
                {
                    if ((int)first.StatusCode == 200)
                    {
                        var landing = await first.Content.ReadAsStringAsync(ct);
                        if (!landing.Contains("登入页面"))
                        {
                            Trace?.Invoke("   已是登录态，跳过认证");
                            return new LoginResult(true, false, null, "已有会话，无需重新登录");
                        }
                    }
                    return new LoginResult(false, false, null, $"教务系统没有跳转（HTTP {(int)first.StatusCode}），接口可能变了");
                }

                var candidate = MakeAbsolute(AppOrigin, location);
                if (candidate.Contains("authserver", StringComparison.OrdinalIgnoreCase))
                {
                    _idpLoginUrl = candidate;
                    break;
                }

                Trace?.Invoke($"   ⚠ 跳到了非认证地址：{Cut(candidate)}");
                if (attempt == 0) continue;
                return new LoginResult(false, false, null,
                    $"教务系统的 SSO 入口把请求跳到了 {candidate}，没有进入统一身份认证平台。",
                    true, $"跳转目标：{candidate}\n可以点「在浏览器里打开」先手动登录一次，再回来点重试。",
                    candidate.Contains("/student/") ? candidate : AppBase + "/login");
            }

            Trace?.Invoke("2) 打开认证页");
            var page = await SendAsync(_idp, new HttpRequestMessage(HttpMethod.Get, _idpLoginUrl), ct);
            var html = await page.Content.ReadAsStringAsync(ct);
            _salt = Match(html, "id=\"pwdEncryptSalt\"[^>]*value=\"([^\"]*)\"") ?? "";
            _execution = Match(html, "id=\"execution\"[^>]*value=\"([^\"]*)\"") ?? "e1s1";
            _lt = Match(html, "id=\"lt\"[^>]*value=\"([^\"]*)\"") ?? "";
            if (_salt.Length == 0)
                return new LoginResult(false, false, null, "认证页里没有找到加密盐值，页面结构可能变了");

            var needUrl = $"{IdpOrigin}/authserver/checkNeedCaptcha.htl?username={Uri.EscapeDataString(username)}&_={DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
            var needReq = new HttpRequestMessage(HttpMethod.Get, needUrl);
            needReq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            needReq.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
            Trace?.Invoke("3) 查询是否需要验证码");
            var needRes = await SendAsync(_idp, needReq, ct);
            var needBody = await needRes.Content.ReadAsStringAsync(ct);
            var needCaptcha = needBody.Contains("\"isNeed\":true", StringComparison.OrdinalIgnoreCase);

            if (needCaptcha && string.IsNullOrEmpty(captcha))
            {
                var img = await GetCaptchaAsync(ct);
                return new LoginResult(false, true, img, "认证平台要求输入验证码");
            }

            Trace?.Invoke("4) 提交账号密码");
            var post = new HttpRequestMessage(HttpMethod.Post,
                $"{IdpOrigin}/authserver/login?service={Uri.EscapeDataString(Service)}")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = username,
                    ["password"] = EncryptPassword(password, _salt),
                    ["passwordText"] = "",
                    ["captcha"] = captcha ?? "",
                    ["lt"] = _lt,
                    ["execution"] = _execution,
                    ["_eventId"] = "submit",
                    ["service"] = Service,   // 少这个参数认证服务器会 500
                    ["cllt"] = "userNameLogin",
                    ["dllt"] = "generalLogin",
                    ["rmShown"] = "1",
                }),
            };
            post.Headers.TryAddWithoutValidation("Referer", _idpLoginUrl);
            var postRes = await SendAsync(_idp, post, ct);
            var redirect = postRes.Headers.Location?.ToString();
            Trace?.Invoke($"   返回 {(int)postRes.StatusCode} redirect={Cut(redirect, 60)}");

            if (string.IsNullOrEmpty(redirect))
            {
                var body = await postRes.Content.ReadAsStringAsync(ct);
                var tip = Match(body, "class=\"success-tip\">([^<]*)<") ?? "";
                var code = (int)postRes.StatusCode;
                var msg = tip.Length > 0
                    ? tip
                    : code == 401
                        ? "认证平台返回 401：账号或密码不被接受（连续输错也可能被暂时锁定，稍后再试）。"
                        : $"认证被拒绝（HTTP {code}）";
                if (needCaptcha) msg += "，可能是验证码不正确";

                if (!needCaptcha && LooksLikeChallenge(code, body, out var detail, out var url))
                    return new LoginResult(false, false, null, detail, true, detail, url);
                return new LoginResult(false, needCaptcha, null, msg);
            }

            Trace?.Invoke("5) 跟随 ticket 回跳");
            var next = new Uri(new Uri(IdpOrigin), redirect).ToString();
            var finalUrl = next;
            var finalStatus = 0;
            for (var hop = 0; hop < 8; hop++)
            {
                var isApp = next.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase);
                var res = await SendAsync(isApp ? _app : _idp, new HttpRequestMessage(HttpMethod.Get, next), ct);
                finalUrl = next;
                finalStatus = (int)res.StatusCode;
                Trace?.Invoke($"   hop{hop}: {finalStatus} {Cut(next, 66)}");
                if (finalStatus is not (301 or 302 or 303 or 307 or 308)) break;
                var loc = res.Headers.Location?.ToString();
                if (string.IsNullOrEmpty(loc)) break;
                next = MakeAbsolute(next, loc);
            }

            // 会话 cookie 都带 Path=/student，必须按该路径查
            var cookieCount = _appCookies.GetCookies(new Uri(AppBase + "/")).Count
                              + _appCookies.GetCookies(new Uri(AppOrigin)).Count;
            Trace?.Invoke($"6) 教务系统 cookie={cookieCount} 落地 {finalStatus} {Cut(finalUrl, 66)}");
            if (cookieCount > 0) return new LoginResult(true, false, null, "登录成功");

            var backToLogin = finalUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) || finalStatus is 401 or 403;
            if (backToLogin)
            {
                var fresh = await GetCaptchaAsync(ct);
                if (fresh.Length > 0)
                    return new LoginResult(false, true, fresh,
                        "认证没有完成：教务系统把请求退回了登录页，一般是验证码不正确或已过期。下面是一张新的验证码，请重新输入。");
            }

            return new LoginResult(false, false, null,
                $"认证流程结束，但没有拿到教务系统会话（落地 {finalUrl}，HTTP {finalStatus}）。",
                true, $"落地地址：{finalUrl}\nHTTP {finalStatus}\n可以点「在浏览器里打开」手动登录一次，再回来点重试。",
                AppBase + "/login");
        }
        catch (Exception ex)
        {
            return new LoginResult(false, false, null, "网络异常：" + ex.Message);
        }
    }

    // ── 取身份与课程上下文（不依赖任何随包数据） ──────────────

    public async Task<BootstrapResult> BootstrapAsync(string? preferSemesterId = null, CancellationToken ct = default)
    {
        try
        {
            Trace?.Invoke("7) 读取课表页（拿 stdPersonId 与学期列表）");
            var page = await GetRawPageAsync("/for-std/course-table", ct);
            if (page.Length < 1000)
                return new BootstrapResult(false, "课表页没有返回内容，会话可能已失效");

            var personMatch = Regex.Match(page, @"stdPersonId['""\]]*\s*=\s*(\d+)");
            var stdPersonId = personMatch.Success ? int.Parse(personMatch.Groups[1].Value) : 0;

            var semesters = new List<(string Id, string Name)>();
            foreach (Match m in Regex.Matches(page, @"<option[^>]*value=""(\d+)""[^>]*>([^<]+)</option>"))
                semesters.Add((m.Groups[1].Value, m.Groups[2].Value.Trim()));
            var selected = Regex.Match(page, @"<option[^>]*selected[^>]*value=""(\d+)""");
            var semesterId = preferSemesterId ?? (selected.Success ? selected.Groups[1].Value : semesters.FirstOrDefault().Id);
            Trace?.Invoke($"   stdPersonId={stdPersonId} 学期 {semesters.Count} 个，选用 {semesterId}");

            // get-data：lessonIds / timeTableLayoutId / currentWeek 都在这里
            var gdReq = new HttpRequestMessage(HttpMethod.Get,
                $"{AppBase}/for-std/course-table/get-data?bizTypeId=2&semesterId={semesterId}");
            gdReq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            gdReq.Headers.TryAddWithoutValidation("Accept", "application/json");
            var gdRes = await SendAsync(_app, gdReq, ct);
            var gdText = await gdRes.Content.ReadAsStringAsync(ct);
            Trace?.Invoke($"8) get-data {(int)gdRes.StatusCode} len={gdText.Length}");

            int[] lessonIds = Array.Empty<int>();
            var layoutId = 0;
            var currentWeek = 0;
            var weekCount = 20;
            try
            {
                using var doc = JsonDocument.Parse(gdText);
                var root = doc.RootElement;
                if (root.TryGetProperty("lessonIds", out var ids))
                    lessonIds = ids.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (root.TryGetProperty("timeTableLayoutId", out var tl)) layoutId = tl.GetInt32();
                if (root.TryGetProperty("currentWeek", out var cw)) currentWeek = cw.GetInt32();
                if (root.TryGetProperty("weekIndices", out var wi)) weekCount = wi.GetArrayLength();
            }
            catch (Exception ex)
            {
                return new BootstrapResult(false, "课表页数据解析失败：" + ex.Message);
            }

            if (lessonIds.Length == 0)
                return new BootstrapResult(false, "这个学期没有查到选课记录（lessonIds 为空）");

            // 真实节次时间表
            var slots = await FetchSlotsAsync(layoutId, ct);
            Trace?.Invoke($"9) 节次表 {slots.Count} 节，课程 {lessonIds.Length} 门次");

            // 学生姓名/班级（仅用于显示）
            var name = "";
            var klass = "";
            try
            {
                var stu = await SendAsync(_app, new HttpRequestMessage(HttpMethod.Get,
                    $"{AppBase}/ws/student/home-page/students"), ct);
                var stuJson = await stu.Content.ReadAsStringAsync(ct);
                using var sd = JsonDocument.Parse(stuJson);
                if (sd.RootElement.TryGetProperty("person", out var p) && p.TryGetProperty("name", out var n))
                    name = n.GetString() ?? "";
                if (sd.RootElement.TryGetProperty("cultivateTypeList", out var list) && list.GetArrayLength() > 0
                    && list[0].TryGetProperty("adminClass", out var ac))
                    klass = ac.GetString() ?? "";
            }
            catch { }

            return new BootstrapResult(true, "已获取课程上下文", stdPersonId, semesterId,
                currentWeek, layoutId, lessonIds, slots, semesters, name, klass, Math.Clamp(weekCount, 1, 30));
        }
        catch (Exception ex)
        {
            return new BootstrapResult(false, "读取课程上下文失败：" + ex.Message);
        }
    }

    private async Task<List<SlotRecord>> FetchSlotsAsync(int layoutId, CancellationToken ct)
    {
        var slots = new List<SlotRecord>();
        if (layoutId <= 0) return ScheduleFile.DefaultSlots();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{AppBase}/ws/schedule-table/timetable-layout")
            {
                Content = new StringContent($"{{\"timeTableLayoutId\":{layoutId}}}", Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            var res = await SendAsync(_app, req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            foreach (var u in doc.RootElement.GetProperty("result").GetProperty("courseUnitList").EnumerateArray())
            {
                slots.Add(new SlotRecord
                {
                    Index = u.GetProperty("indexNo").GetInt32(),
                    Start = Hhmm(u.GetProperty("startTime").GetInt32()),
                    End = Hhmm(u.GetProperty("endTime").GetInt32()),
                });
            }
        }
        catch { }
        return slots.Count > 0 ? slots : ScheduleFile.DefaultSlots();
    }

    // ── 抓取整学期 ──────────────────────────────────────────

    public async Task<FetchResult> FetchAllAsync(TermRecord term, string semesterId, int stdPersonId,
        int[] lessonIds, List<SlotRecord> slots, IProgress<(int week, int total, string message)>? progress = null,
        CancellationToken ct = default)
    {
        var merged = new Dictionary<string, LessonRecord>();
        var weeks = Math.Clamp(term.TotalWeeks, 1, 30);

        for (var week = 1; week <= weeks; week++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((week, weeks, $"正在抓取第 {week} 周…"));

            var body = JsonSerializer.Serialize(new
            {
                lessonIds,
                studentId = (string?)null,   // 与网页版一致：只靠 stdPersonId 过滤
                semesterId,
                stdPersonId,
                weekIndex = week.ToString(),
            });

            var req = new HttpRequestMessage(HttpMethod.Post, $"{AppBase}/ws/schedule-table/datum")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            req.Headers.TryAddWithoutValidation("Referer", $"{AppBase}/for-std/course-table");

            if (week <= 1) Trace?.Invoke($"   请求体 {Cut(body, 170)}");
            var res = await SendAsync(_app, req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (week <= 1) Trace?.Invoke($"   响应 {(int)res.StatusCode} len={text.Length} {Cut(text.Replace("\n", ""), 180)}");

            if (!text.TrimStart().StartsWith('{'))
            {
                if (LooksLikeChallenge((int)res.StatusCode, text, out var detail, out var url))
                    return new FetchResult(false, detail, new(), "", true, detail, url);
                return new FetchResult(false, $"第 {week} 周返回异常，已中止（会话可能失效）", new(), "");
            }

            foreach (var lesson in ParseWeek(text, term, week, slots, lessonIds))
            {
                var key = $"{lesson.Name}|{lesson.Day}|{lesson.StartSlot}|{lesson.SlotCount}";
                if (merged.TryGetValue(key, out var exist))
                    exist.Weeks = exist.Weeks.Union(lesson.Weeks).OrderBy(w => w).ToList();
                else
                    merged[key] = lesson;
            }
        }

        var list = merged.Values.OrderBy(l => l.Day).ThenBy(l => l.StartSlot).ToList();
        return new FetchResult(true, $"{list.Count} 门次 / {list.Sum(l => l.Weeks.Count)} 节", list,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
    }

    private static List<LessonRecord> ParseWeek(string json, TermRecord term, int week,
        List<SlotRecord> slots, int[] lessonIds)
    {
        var result = new List<LessonRecord>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.GetProperty("result");
        var lessons = root.GetProperty("lessonList").EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetInt32(), e => e);

        foreach (var s in root.GetProperty("scheduleList").EnumerateArray())
        {
            var lessonId = s.GetProperty("lessonId").GetInt32();
            if (!lessons.TryGetValue(lessonId, out var info)) continue;

            var start = s.GetProperty("startTime").GetInt32();
            var end = s.GetProperty("endTime").GetInt32();
            var periods = s.TryGetProperty("periods", out var p) ? Math.Max(1, p.GetInt32()) : 2;
            var day = s.GetProperty("weekday").GetInt32();
            var name = info.TryGetProperty("courseName", out var cn) ? cn.GetString() ?? "" : "";
            var room = s.TryGetProperty("room", out var rm) && rm.ValueKind == JsonValueKind.Object
                       && rm.TryGetProperty("nameZh", out var rz) ? rz.GetString() ?? "" : "待定";
            var teacher = s.TryGetProperty("personName", out var pn) ? pn.GetString() ?? "" : "";

            // 用真实节次表把 HHMM 映射到节次索引
            var startSlot = WeekMath.SlotForTime(slots, Hhmm(start));

            result.Add(new LessonRecord
            {
                Id = $"{lessonId}-{day}-{start}",
                Name = name,
                Teacher = teacher,
                Room = room,
                Day = day,
                StartSlot = startSlot,
                SlotCount = periods,
                Weeks = new List<int> { week },
                ColorKey = LessonColors.KeyOf(name, Palette.Get("blue")),
                SourceTime = $"{Hhmm(start)}-{Hhmm(end)}",
                SourceCode = info.TryGetProperty("code", out var code) ? code.GetString() : null,
                SourceType = info.TryGetProperty("courseTypeName", out var tp) ? tp.GetString() : null,
                SourceClass = info.TryGetProperty("name", out var nm) ? nm.GetString() : null,
                IsCustom = false,
            });
        }
        return result;
    }

    private static string Hhmm(int value) => $"{value / 100:00}:{value % 100:00}";

    // ── 反爬识别：一律交给用户 ──────────────────────────────

    private static bool LooksLikeChallenge(int status, string body, out string detail, out string url)
    {
        detail = ""; url = "";
        var head = body.Length > 400 ? body[..400] : body;

        if (status is 403 or 429 or 503)
        {
            detail = $"服务器返回 HTTP {status}（可能是访问频率限制或风控拦截）。";
            url = AppBase + "/login";
            return true;
        }
        if (head.Contains("verifySliderCaptcha", StringComparison.OrdinalIgnoreCase)
            || head.Contains("sliderCaptcha", StringComparison.OrdinalIgnoreCase)
            || head.Contains("toSliderCaptcha", StringComparison.OrdinalIgnoreCase)
            || head.Contains("滑块"))
        {
            detail = "认证平台要求完成滑块验证。";
            url = IdpOrigin + "/authserver/login";
            return true;
        }
        foreach (var word in new[] { "安全验证", "人机验证", "访问过于频繁", "操作频繁", "风控", "拦截", "验证码错误次数" })
        {
            if (head.Contains(word, StringComparison.Ordinal))
            {
                detail = $"服务器返回提示：「{word}」。";
                url = AppBase + "/login";
                return true;
            }
        }
        return false;
    }

    // ── 工具 ────────────────────────────────────────────────

    /// <summary>与认证页 encrypt.js 等价：明文前补 64 位随机串，AES-128-CBC/PKCS7，输出 base64。</summary>
    public static string EncryptPassword(string password, string salt)
    {
        const string chars = "ABCDEFGHJKMNPQRSTWXYZabcdefhijkmnprstwxyz2345678";
        var rnd = new Random();
        string Rand(int n) => new(Enumerable.Range(0, n).Select(_ => chars[rnd.Next(chars.Length)]).ToArray());

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.UTF8.GetBytes(salt.Trim());
        aes.IV = Encoding.UTF8.GetBytes(Rand(16));
        using var enc = aes.CreateEncryptor();
        var plain = Encoding.UTF8.GetBytes(Rand(64) + password);
        return Convert.ToBase64String(enc.TransformFinalBlock(plain, 0, plain.Length));
    }

    private async Task<byte[]> GetCaptchaAsync(CancellationToken ct)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                $"{IdpOrigin}/authserver/getCaptcha.htl?{DateTimeOffset.Now.ToUnixTimeMilliseconds()}");
            req.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/*,*/*;q=0.8");
            var res = await SendAsync(_idp, req, ct);
            return await res.Content.ReadAsByteArrayAsync(ct);
        }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>用已登录的会话取一个页面原文（诊断/引导用）。</summary>
    public async Task<string> GetRawPageAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, AppBase + path);
            var res = await SendAsync(_app, req, ct);
            return await res.Content.ReadAsStringAsync(ct);
        }
        catch { return ""; }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken ct)
    {
        var res = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (res.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var v in values)
            {
                var head = v.Length > 60 ? v[..60] + "…" : v;
                RawSetCookies.Add(head);
                Trace?.Invoke($"   [set-cookie] {head}");
            }
        }
        return res;
    }

    private static string? Match(string input, string pattern)
    {
        var m = Regex.Match(input, pattern);
        return m.Success ? m.Groups[1].Value : null;
    }

    public void Dispose()
    {
        _app.Dispose();
        _idp.Dispose();
    }
}

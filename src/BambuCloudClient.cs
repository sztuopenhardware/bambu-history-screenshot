using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BambuHistoryScreenshot
{
    public enum CloudLoginState
    {
        Success,
        NeedVerifyCode,
        NeedTfa,
        Failed
    }

    public class CloudLoginResult
    {
        public CloudLoginState State { get; set; }
        public string AccessToken { get; set; }
        public string TfaKey { get; set; }
        public string Message { get; set; }
        public bool CodeAutoSent { get; set; }

        public CloudLoginResult()
        {
            AccessToken = string.Empty;
            TfaKey = string.Empty;
            Message = string.Empty;
        }
    }

    public class CloudSmsSendResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }

        public CloudSmsSendResult()
        {
            Message = string.Empty;
        }
    }

    public class BambuCloudClient
    {
        private readonly JavaScriptSerializer _json;
        private readonly CookieContainer _cookies;
        private readonly HttpClient _http;

        public string Region { get; private set; } // "cn" or "global"
        public string AccessToken { get; private set; }
        public string Account { get; private set; }

        public bool IsLoggedIn
        {
            get { return !string.IsNullOrEmpty(AccessToken); }
        }

        public string ApiBaseUrl
        {
            get { return Region == "global" ? "https://api.bambulab.com" : "https://api.bambulab.cn"; }
        }

        public string WebBaseUrl
        {
            get { return Region == "global" ? "https://bambulab.com" : "https://bambulab.cn"; }
        }

        public string RegionDisplayName
        {
            get { return Region == "global" ? "国际区 (api.bambulab.com)" : "中国大陆 (api.bambulab.cn)"; }
        }

        public BambuCloudClient(string region)
        {
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            Region = (region == "global") ? "global" : "cn";
            AccessToken = string.Empty;
            Account = string.Empty;

            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = 32 * 1024 * 1024;

            _cookies = new CookieContainer();
            HttpClientHandler handler = new HttpClientHandler();
            handler.CookieContainer = _cookies;
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            _http = new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(25);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "bambu_network_agent/01.09.05.01");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        public void SetRegion(string region)
        {
            Region = (region == "global") ? "global" : "cn";
        }

        public void SetToken(string token, string region, string accountLabel)
        {
            SetRegion(region);
            string cleaned = (token ?? string.Empty).Trim();
            if (cleaned.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned.Substring(7).Trim();
            }
            AccessToken = cleaned;
            Account = string.IsNullOrWhiteSpace(accountLabel) ? "Token登录" : accountLabel.Trim();
        }

        public void Logout()
        {
            AccessToken = string.Empty;
        }

        public async Task<CloudLoginResult> LoginAsync(string account, string password)
        {
            Account = (account ?? string.Empty).Trim();
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["account"] = Account;
            payload["password"] = password ?? string.Empty;
            payload["apiError"] = string.Empty;

            CloudLoginResult res = await PostLoginPayloadAsync(payload);
            if (res.State == CloudLoginState.NeedVerifyCode)
            {
                if (Account.Contains("@"))
                {
                    bool sent = await SendEmailCodeAsync(Account);
                    res.CodeAutoSent = sent;
                    if (sent)
                    {
                        res.Message = "需要邮箱验证码，已自动向 " + Account + " 发送 6 位验证码，请查收并输入。";
                    }
                    else
                    {
                        res.Message = "需要验证码登录，请检查邮箱收件箱或点击“重发验证码”。";
                    }
                }
                else if (IsPhoneLike(Account))
                {
                    CloudSmsSendResult sms = await SendSmsCodeAsync(Account, "+86");
                    res.CodeAutoSent = sms.Success;
                    res.Message = sms.Success
                        ? ("需要短信验证码，已自动向 " + Account + " 发送短信验证码，请输入。")
                        : ("需要短信验证码（" + sms.Message + "），可点击“重发验证码”或切换到“手机短信验证码登录”。");
                }
            }
            return res;
        }

        public static bool IsPhoneLike(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
            if (t.StartsWith("+")) t = t.Substring(1);
            if (t.Length < 7 || t.Length > 15) return false;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] < '0' || t[i] > '9') return false;
            }
            return true;
        }

        public static string NormalizePhoneForRequest(string rawPhone, string areaCode, bool includeAreaPrefix)
        {
            string p = (rawPhone ?? string.Empty).Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
            string ac = (areaCode ?? "+86").Trim();
            if (!string.IsNullOrEmpty(ac) && !ac.StartsWith("+")) ac = "+" + ac;

            if (p.StartsWith("+"))
            {
                if (!includeAreaPrefix && p.StartsWith("+86") && p.Length > 3)
                {
                    return p.Substring(3);
                }
                return p;
            }

            if (includeAreaPrefix)
            {
                return ac + p;
            }
            if (ac == "+86")
            {
                return p;
            }
            return ac + p;
        }

        public async Task<CloudSmsSendResult> SendSmsCodeAsync(string phone, string areaCode)
        {
            CloudSmsSendResult result = new CloudSmsSendResult();
            string primaryPhone = NormalizePhoneForRequest(phone, areaCode, false);
            string altPhone = NormalizePhoneForRequest(phone, areaCode, true);

            // Attempt 1: /v1/user-service/user/sendsmscode with primary phone format (e.g. 186xxxxxxxx)
            CloudSmsSendResult first = await TryPostSmsCodeAsync("/v1/user-service/user/sendsmscode", primaryPhone);
            if (first.Success)
            {
                return first;
            }

            // Attempt 2: retry with +86 prefix if different
            if (!string.Equals(altPhone, primaryPhone, StringComparison.Ordinal))
            {
                CloudSmsSendResult second = await TryPostSmsCodeAsync("/v1/user-service/user/sendsmscode", altPhone);
                if (second.Success)
                {
                    return second;
                }
            }

            // Attempt 3: alternate endpoint /v1/user-service/user/sendphone/code
            CloudSmsSendResult third = await TryPostSmsCodeAsync("/v1/user-service/user/sendphone/code", primaryPhone);
            if (third.Success)
            {
                return third;
            }

            result.Success = false;
            result.Message = !string.IsNullOrEmpty(first.Message) ? first.Message : "发送短信验证码失败，请检查手机号与区域设置。";
            return result;
        }

        private async Task<CloudSmsSendResult> TryPostSmsCodeAsync(string relativePath, string phoneValue)
        {
            CloudSmsSendResult res = new CloudSmsSendResult();
            try
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["phone"] = phoneValue;
                payload["type"] = "codeLogin";

                string body = _json.Serialize(payload);
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, ApiBaseUrl + relativePath))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = await _http.SendAsync(req))
                    {
                        string text = await resp.Content.ReadAsStringAsync();
                        if (resp.IsSuccessStatusCode)
                        {
                            Dictionary<string, object> dict = TryParseDict(text);
                            // Check if JSON explicitly signals failure despite HTTP 200
                            if (dict != null && dict.ContainsKey("success") && dict["success"] is bool && !(bool)dict["success"])
                            {
                                res.Success = false;
                                res.Message = ExtractErrorMessage(text);
                                return res;
                            }
                            res.Success = true;
                            res.Message = "短信验证码已发送至 " + phoneValue + "，请注意查收。";
                            return res;
                        }

                        string detail = ExtractErrorMessage(text);
                        res.Success = false;
                        res.Message = string.IsNullOrEmpty(detail)
                            ? ("HTTP " + (int)resp.StatusCode)
                            : ("HTTP " + (int)resp.StatusCode + "：" + detail);
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                res.Success = false;
                res.Message = "网络异常：" + ex.Message;
                return res;
            }
        }

        public async Task<CloudLoginResult> LoginWithSmsCodeAsync(string phone, string areaCode, string code)
        {
            string cleanCode = (code ?? string.Empty).Trim();
            string primaryPhone = NormalizePhoneForRequest(phone, areaCode, false);
            string altPhone = NormalizePhoneForRequest(phone, areaCode, true);
            Account = primaryPhone;

            // Attempt 1: /v1/user-service/user/login with primary phone + code
            Dictionary<string, object> p1 = new Dictionary<string, object>();
            p1["account"] = primaryPhone;
            p1["code"] = cleanCode;
            CloudLoginResult r1 = await PostLoginPayloadAsync(p1);
            if (r1.State == CloudLoginState.Success || r1.State == CloudLoginState.NeedTfa)
            {
                return r1;
            }

            // Attempt 2: /v1/user-service/user/login with +86 prefixed phone + code
            if (!string.Equals(altPhone, primaryPhone, StringComparison.Ordinal))
            {
                Dictionary<string, object> p2 = new Dictionary<string, object>();
                p2["account"] = altPhone;
                p2["code"] = cleanCode;
                CloudLoginResult r2 = await PostLoginPayloadAsync(p2);
                if (r2.State == CloudLoginState.Success || r2.State == CloudLoginState.NeedTfa)
                {
                    Account = altPhone;
                    return r2;
                }
            }

            // Attempt 3: Web sign-in form endpoint ({WebBaseUrl}/api/sign-in/form)
            CloudLoginResult r3 = await TryWebFormCodeLoginAsync(primaryPhone, cleanCode);
            if (r3.State == CloudLoginState.Success)
            {
                return r3;
            }

            return r1;
        }

        private async Task<CloudLoginResult> TryWebFormCodeLoginAsync(string account, string code)
        {
            CloudLoginResult res = new CloudLoginResult();
            try
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["account"] = account;
                payload["code"] = code;

                string body = _json.Serialize(payload);
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, WebBaseUrl + "/api/sign-in/form"))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = await _http.SendAsync(req))
                    {
                        string text = await resp.Content.ReadAsStringAsync();
                        string token = ExtractTokenFromResponseOrCookies(text, new Uri(WebBaseUrl));
                        if (!string.IsNullOrEmpty(token))
                        {
                            AccessToken = token;
                            res.State = CloudLoginState.Success;
                            res.AccessToken = token;
                            res.Message = "短信验证码登录成功！";
                            return res;
                        }
                        res.State = CloudLoginState.Failed;
                        res.Message = ExtractErrorMessage(text);
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                res.State = CloudLoginState.Failed;
                res.Message = ex.Message;
                return res;
            }
        }

        public async Task<CloudLoginResult> LoginWithVerifyCodeAsync(string account, string password, string code)
        {
            Account = (account ?? string.Empty).Trim();
            string cleanCode = (code ?? string.Empty).Trim();

            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["account"] = Account;
            payload["code"] = cleanCode;

            CloudLoginResult res = await PostLoginPayloadAsync(payload);
            if (res.State != CloudLoginState.Success && !string.IsNullOrEmpty(password))
            {
                // Fallback payload variant containing password + code
                Dictionary<string, object> payload2 = new Dictionary<string, object>();
                payload2["account"] = Account;
                payload2["password"] = password;
                payload2["code"] = cleanCode;
                CloudLoginResult res2 = await PostLoginPayloadAsync(payload2);
                if (res2.State == CloudLoginState.Success)
                {
                    return res2;
                }
            }
            return res;
        }

        public async Task<bool> SendEmailCodeAsync(string email)
        {
            try
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["email"] = (email ?? string.Empty).Trim();
                payload["type"] = "codeLogin";

                string body = _json.Serialize(payload);
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, ApiBaseUrl + "/v1/user-service/user/sendemail/code"))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = await _http.SendAsync(req))
                    {
                        return resp.IsSuccessStatusCode;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public async Task<CloudLoginResult> VerifyTfaAsync(string tfaKey, string tfaCode)
        {
            CloudLoginResult res = new CloudLoginResult();
            try
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["tfaKey"] = tfaKey ?? string.Empty;
                payload["tfaCode"] = (tfaCode ?? string.Empty).Trim();

                string body = _json.Serialize(payload);
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, WebBaseUrl + "/api/sign-in/tfa"))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = await _http.SendAsync(req))
                    {
                        string text = await resp.Content.ReadAsStringAsync();
                        string token = ExtractTokenFromResponseOrCookies(text, new Uri(WebBaseUrl));
                        if (!string.IsNullOrEmpty(token))
                        {
                            AccessToken = token;
                            res.State = CloudLoginState.Success;
                            res.AccessToken = token;
                            res.Message = "二次验证成功！";
                            return res;
                        }

                        res.State = CloudLoginState.Failed;
                        res.Message = "二次验证失败（HTTP " + (int)resp.StatusCode + "）：" + ExtractErrorMessage(text);
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                res.State = CloudLoginState.Failed;
                res.Message = "二次验证请求异常：" + ex.Message;
                return res;
            }
        }

        private async Task<CloudLoginResult> PostLoginPayloadAsync(Dictionary<string, object> payload)
        {
            CloudLoginResult res = new CloudLoginResult();
            try
            {
                string body = _json.Serialize(payload);
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, ApiBaseUrl + "/v1/user-service/user/login"))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = await _http.SendAsync(req))
                    {
                        string text = await resp.Content.ReadAsStringAsync();
                        Dictionary<string, object> dict = TryParseDict(text);

                        string token = GetStr(dict, "accessToken");
                        if (!string.IsNullOrEmpty(token))
                        {
                            AccessToken = token;
                            res.State = CloudLoginState.Success;
                            res.AccessToken = token;
                            res.Message = "登录成功！";
                            return res;
                        }

                        string loginType = GetStr(dict, "loginType");
                        string tfaKey = GetStr(dict, "tfaKey");

                        if (string.Equals(loginType, "verifyCode", StringComparison.OrdinalIgnoreCase))
                        {
                            res.State = CloudLoginState.NeedVerifyCode;
                            res.Message = "需要进行验证码校验，请输入收到的 6 位验证码。";
                            return res;
                        }

                        if (string.Equals(loginType, "tfa", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(tfaKey))
                        {
                            res.State = CloudLoginState.NeedTfa;
                            res.TfaKey = tfaKey;
                            res.Message = "账号已开启两步验证（2FA），请输入身份验证器中的 6 位动态码。";
                            return res;
                        }

                        res.State = CloudLoginState.Failed;
                        string errDetail = ExtractErrorMessage(text);
                        res.Message = string.IsNullOrEmpty(errDetail)
                            ? ("登录失败（HTTP " + (int)resp.StatusCode + "），请检查账号、密码及所选服务器区域是否正确。")
                            : ("登录失败：" + errDetail);
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                res.State = CloudLoginState.Failed;
                res.Message = "网络请求失败：" + ex.Message;
                return res;
            }
        }

        private string ExtractTokenFromResponseOrCookies(string responseBody, Uri baseUri)
        {
            Dictionary<string, object> dict = TryParseDict(responseBody);
            string token = GetStr(dict, "accessToken");
            if (!string.IsNullOrEmpty(token)) return token;
            token = GetStr(dict, "token");
            if (!string.IsNullOrEmpty(token)) return token;

            try
            {
                CookieCollection cc = _cookies.GetCookies(baseUri);
                for (int i = 0; i < cc.Count; i++)
                {
                    if (string.Equals(cc[i].Name, "token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(cc[i].Value))
                    {
                        return cc[i].Value;
                    }
                }
            }
            catch
            {
            }
            return string.Empty;
        }

        public async Task<List<PrintHistoryCard>> FetchPrintHistoryAsync(int maxCount, Action<string> progressLog)
        {
            if (string.IsNullOrEmpty(AccessToken))
            {
                throw new InvalidOperationException("尚未登录拓竹云端账号。");
            }

            List<PrintHistoryCard> allTasks = new List<PrintHistoryCard>();
            HashSet<long> seenIds = new HashSet<long>();
            int limit = 50;
            long? afterId = null;
            int offset = 0;
            bool useOffsetMode = false;
            int totalExpected = int.MaxValue;
            int maxPages = Math.Max(1, (maxCount / limit) + 3);

            for (int page = 0; page < maxPages && allTasks.Count < maxCount; page++)
            {
                string url;
                if (page == 0)
                {
                    url = string.Format("{0}/v1/user-service/my/tasks?limit={1}", ApiBaseUrl, limit);
                }
                else if (useOffsetMode)
                {
                    url = string.Format("{0}/v1/user-service/my/tasks?limit={1}&offset={2}", ApiBaseUrl, limit, offset);
                }
                else
                {
                    url = string.Format("{0}/v1/user-service/my/tasks?limit={1}&after={2}", ApiBaseUrl, limit, afterId.Value);
                }

                Dictionary<string, object> respDict = await GetAuthorizedJsonAsync(url);
                if (respDict == null)
                {
                    break;
                }

                int totalFromResp = ExtractTotalCount(respDict);
                if (totalFromResp > 0)
                {
                    totalExpected = totalFromResp;
                }

                IList hits = respDict.ContainsKey("hits") ? (respDict["hits"] as IList) : null;
                if (hits == null || hits.Count == 0)
                {
                    break;
                }

                int addedThisPage = 0;
                long lastIdThisPage = 0;
                for (int i = 0; i < hits.Count; i++)
                {
                    Dictionary<string, object> item = hits[i] as Dictionary<string, object>;
                    if (item == null) continue;

                    long id = GetLong(item, "id");
                    if (id > 0) lastIdThisPage = id;

                    if (id > 0 && seenIds.Contains(id))
                    {
                        continue;
                    }
                    if (id > 0) seenIds.Add(id);

                    PrintHistoryCard card = ParseTaskToCard(item);
                    if (card != null)
                    {
                        allTasks.Add(card);
                        addedThisPage++;
                        if (allTasks.Count >= maxCount) break;
                    }
                }

                if (progressLog != null)
                {
                    string totalStr = totalExpected < int.MaxValue ? totalExpected.ToString() : "?";
                    progressLog(string.Format("云端同步进度：已获取 {0} / {1} 条打印记录...", allTasks.Count, totalStr));
                }

                // If `after=` returned only duplicates on page 1, automatically retry once with `offset=`
                if (addedThisPage == 0)
                {
                    if (!useOffsetMode && page == 1)
                    {
                        useOffsetMode = true;
                        offset = allTasks.Count;
                        continue;
                    }
                    break;
                }

                if (allTasks.Count >= totalExpected || hits.Count < limit)
                {
                    // Still allow one more check if API enforces a smaller max page size (e.g. 20)
                    if (allTasks.Count >= totalExpected)
                    {
                        break;
                    }
                }

                afterId = lastIdThisPage;
                offset = allTasks.Count;

                // Polite delay to avoid rate limits
                await Task.Delay(220);
            }

            return allTasks;
        }

        private async Task<Dictionary<string, object>> GetAuthorizedJsonAsync(string url)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
                using (HttpResponseMessage resp = await _http.SendAsync(req))
                {
                    string text = await resp.Content.ReadAsStringAsync();
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw new UnauthorizedAccessException("云端登录已失效或 Token 无效（HTTP " + (int)resp.StatusCode + "），请重新登录。");
                    }
                    if (!resp.IsSuccessStatusCode)
                    {
                        throw new Exception("云端接口返回错误（HTTP " + (int)resp.StatusCode + "）：" + ExtractErrorMessage(text));
                    }
                    return TryParseDict(text);
                }
            }
        }

        private static int ExtractTotalCount(Dictionary<string, object> dict)
        {
            if (dict == null || !dict.ContainsKey("total") || dict["total"] == null) return 0;
            object t = dict["total"];
            if (t is int) return (int)t;
            if (t is long) return (int)(long)t;
            Dictionary<string, object> tDict = t as Dictionary<string, object>;
            if (tDict != null && tDict.ContainsKey("value"))
            {
                return (int)GetLong(tDict, "value");
            }
            int parsed;
            if (int.TryParse(t.ToString(), out parsed)) return parsed;
            return 0;
        }

        public static PrintHistoryCard ParseTaskToCard(Dictionary<string, object> item)
        {
            if (item == null) return null;

            PrintHistoryCard card = new PrintHistoryCard();
            card.CloudTaskId = GetLong(item, "id");

            string designTitle = GetStr(item, "designTitle");
            string title = GetStr(item, "title");
            card.Name = !string.IsNullOrWhiteSpace(designTitle) ? designTitle.Trim() : title.Trim();
            if (string.IsNullOrWhiteSpace(card.Name))
            {
                card.Name = "未命名任务_" + card.CloudTaskId;
            }

            card.CoverImageUrl = GetStr(item, "cover");

            // Device
            string deviceName = GetStr(item, "deviceName");
            string deviceModel = GetStr(item, "deviceModel");
            if (!string.IsNullOrWhiteSpace(deviceName) && !string.IsNullOrWhiteSpace(deviceModel) &&
                deviceName.IndexOf(deviceModel, StringComparison.OrdinalIgnoreCase) < 0)
            {
                card.Device = string.Format("{0}({1})", deviceName.Trim(), deviceModel.Trim());
            }
            else
            {
                card.Device = !string.IsNullOrWhiteSpace(deviceName) ? deviceName.Trim() : deviceModel.Trim();
            }

            // Plate
            long plateIndex = GetLong(item, "plateIndex");
            string plateName = GetStr(item, "plateName").Trim();
            if (plateIndex > 0)
            {
                card.Plate = "第 " + plateIndex + " 盘";
                if (!string.IsNullOrEmpty(plateName))
                {
                    card.Plate += " - " + plateName;
                }
            }
            else if (!string.IsNullOrEmpty(plateName))
            {
                card.Plate = plateName;
            }

            // StartTime & EndTime
            DateTime startDt;
            string startStr = GetStr(item, "startTime");
            if (TryParseIsoDateToLocal(startStr, out startDt))
            {
                card.PrintDate = startDt;
                card.HasTime = true;
                card.RawDateText = "(" + startDt.ToString("yyyy/MM/dd HH:mm") + ")";
            }

            // Status
            card.Status = ParseStatusText(item.ContainsKey("status") ? item["status"] : null);

            // CostTime (seconds)
            double costSec = GetDouble(item, "costTime");
            DateTime endDt;
            string endStr = GetStr(item, "endTime");
            if ((card.Status == "成功" || card.Status == "已取消") &&
                card.PrintDate != DateTime.MinValue &&
                TryParseIsoDateToLocal(endStr, out endDt) &&
                endDt > card.PrintDate)
            {
                costSec = (endDt - card.PrintDate).TotalSeconds;
            }
            card.Duration = FormatDuration(costSec);

            // Filament type(s) & weight(g) from `weight` + `amsDetailMapping`
            ParseFilamentInfo(item, card);

            card.DetectionTier = "Cloud-API";
            card.IsFullyVisible = true;
            card.CaptureState = "云端同步(未截图)";
            card.UniqueKey = card.BuildUniqueKey();
            return card;
        }

        private static void ParseFilamentInfo(Dictionary<string, object> item, PrintHistoryCard card)
        {
            List<string> orderedTypes = new List<string>();
            Dictionary<string, double> weightByType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            double amsWeightSum = 0;

            IList amsList = item.ContainsKey("amsDetailMapping") ? (item["amsDetailMapping"] as IList) : null;
            if (amsList != null)
            {
                for (int i = 0; i < amsList.Count; i++)
                {
                    Dictionary<string, object> slot = amsList[i] as Dictionary<string, object>;
                    if (slot == null) continue;

                    string targetType = GetStr(slot, "targetFilamentType").Trim();
                    string srcType = GetStr(slot, "filamentType").Trim();
                    string fType = !string.IsNullOrEmpty(targetType) ? targetType : srcType;
                    double w = GetDouble(slot, "weight");
                    if (w > 0) amsWeightSum += w;

                    if (!string.IsNullOrEmpty(fType))
                    {
                        if (!weightByType.ContainsKey(fType))
                        {
                            orderedTypes.Add(fType);
                            weightByType[fType] = 0;
                        }
                        if (w > 0)
                        {
                            weightByType[fType] += w;
                        }
                    }
                }
            }

            if (orderedTypes.Count > 0)
            {
                card.FilamentTypes = string.Join(" / ", orderedTypes.ToArray());

                List<string> detailParts = new List<string>();
                for (int i = 0; i < orderedTypes.Count; i++)
                {
                    string t = orderedTypes[i];
                    double w = weightByType[t];
                    if (w > 0)
                    {
                        detailParts.Add(string.Format("{0} {1}g", t, w.ToString("0.##", CultureInfo.InvariantCulture)));
                    }
                    else
                    {
                        detailParts.Add(t);
                    }
                }
                card.FilamentDetail = string.Join(" + ", detailParts.ToArray());
            }

            bool hasTaskWeightKey = item.ContainsKey("weight") && item["weight"] != null;
            double taskWeight = GetDouble(item, "weight");

            if (taskWeight > 0)
            {
                card.FilamentWeightGrams = Math.Round(taskWeight, 2);
                card.HasFilamentWeight = true;
            }
            else if (amsWeightSum > 0)
            {
                card.FilamentWeightGrams = Math.Round(amsWeightSum, 2);
                card.HasFilamentWeight = true;
            }
            else if (hasTaskWeightKey)
            {
                card.FilamentWeightGrams = 0;
                card.HasFilamentWeight = true;
            }
        }

        public static int EnrichCardsWithCloudData(List<PrintHistoryCard> targetCards, List<PrintHistoryCard> cloudTasks)
        {
            if (targetCards == null || cloudTasks == null || targetCards.Count == 0 || cloudTasks.Count == 0)
            {
                return 0;
            }

            int matched = 0;
            for (int i = 0; i < targetCards.Count; i++)
            {
                PrintHistoryCard local = targetCards[i];
                PrintHistoryCard best = FindBestCloudMatch(local, cloudTasks);
                if (best != null)
                {
                    if (!string.IsNullOrEmpty(best.FilamentTypes))
                    {
                        local.FilamentTypes = best.FilamentTypes;
                    }
                    if (!string.IsNullOrEmpty(best.FilamentDetail))
                    {
                        local.FilamentDetail = best.FilamentDetail;
                    }
                    if (best.HasFilamentWeight)
                    {
                        local.FilamentWeightGrams = best.FilamentWeightGrams;
                        local.HasFilamentWeight = true;
                    }
                    local.CloudTaskId = best.CloudTaskId;
                    if (string.IsNullOrEmpty(local.CoverImageUrl))
                    {
                        local.CoverImageUrl = best.CoverImageUrl;
                    }
                    if (string.IsNullOrEmpty(local.Status) && !string.IsNullOrEmpty(best.Status))
                    {
                        local.Status = best.Status;
                    }
                    matched++;
                }
            }
            return matched;
        }

        private static PrintHistoryCard FindBestCloudMatch(PrintHistoryCard local, List<PrintHistoryCard> cloudTasks)
        {
            if (local == null) return null;

            // Fast exact CloudTaskId match
            if (local.CloudTaskId > 0)
            {
                for (int i = 0; i < cloudTasks.Count; i++)
                {
                    if (cloudTasks[i].CloudTaskId == local.CloudTaskId) return cloudTasks[i];
                }
            }

            PrintHistoryCard best = null;
            int bestScore = 0;
            string localName = NormalizeNameForMatch(local.Name);

            for (int i = 0; i < cloudTasks.Count; i++)
            {
                PrintHistoryCard c = cloudTasks[i];
                int score = 0;

                if (local.PrintDate != DateTime.MinValue && c.PrintDate != DateTime.MinValue)
                {
                    if (local.HasTime)
                    {
                        double diffMin = Math.Abs((local.PrintDate - c.PrintDate).TotalMinutes);
                        if (diffMin <= 1.5) score += 60;
                        else if (diffMin <= 5.0) score += 35;
                        else continue;
                    }
                    else
                    {
                        if (local.PrintDate.Date == c.PrintDate.Date) score += 25;
                        else continue;
                    }
                }

                string cloudName = NormalizeNameForMatch(c.Name);
                if (!string.IsNullOrEmpty(localName) && !string.IsNullOrEmpty(cloudName))
                {
                    if (string.Equals(localName, cloudName, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 50;
                    }
                    else if (cloudName.StartsWith(localName, StringComparison.OrdinalIgnoreCase) ||
                             localName.StartsWith(cloudName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Handles CSS ellipsis truncation like "COMPOUND_6 + ..."
                        score += 40;
                    }
                }

                if (!string.IsNullOrEmpty(local.Plate) && !string.IsNullOrEmpty(c.Plate) &&
                    string.Equals(local.Plate.Replace(" ", ""), c.Plate.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                {
                    score += 15;
                }

                if (!string.IsNullOrEmpty(local.Device) && !string.IsNullOrEmpty(c.Device) &&
                    (c.Device.IndexOf(local.Device, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     local.Device.IndexOf(c.Device, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    score += 10;
                }

                int minThreshold = local.HasTime ? 75 : 65;
                if (score >= minThreshold && score > bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }

            return best;
        }

        private static string NormalizeNameForMatch(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            string s = name.Trim();
            if (s.EndsWith("...")) s = s.Substring(0, s.Length - 3).Trim();
            if (s.EndsWith("…")) s = s.Substring(0, s.Length - 1).Trim();
            return s;
        }

        private static string ParseStatusText(object statusVal)
        {
            if (statusVal == null) return string.Empty;
            int intStatus;
            if (int.TryParse(statusVal.ToString(), out intStatus))
            {
                switch (intStatus)
                {
                    case 2: return "成功";
                    case 3: return "已取消";
                    case 4: return "失败";
                    default: return "正在打印";
                }
            }

            string s = statusVal.ToString().Trim().ToLowerInvariant();
            if (s == "finish" || s == "finished" || s == "success" || s == "completed") return "成功";
            if (s == "cancel" || s == "canceled" || s == "cancelled") return "已取消";
            if (s == "fail" || s == "failed") return "失败";
            if (s == "running" || s == "printing") return "正在打印";
            return statusVal.ToString().Trim();
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return string.Empty;
            if (seconds >= 3600)
            {
                return (Math.Round((seconds / 3600.0) * 10.0) / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "h";
            }
            if (seconds >= 60)
            {
                return ((int)Math.Floor(seconds / 60.0)).ToString(CultureInfo.InvariantCulture) + "min";
            }
            return ((int)Math.Round(seconds)).ToString(CultureInfo.InvariantCulture) + "s";
        }

        private static bool TryParseIsoDateToLocal(string text, out DateTime localDt)
        {
            localDt = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(text)) return false;
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
            {
                localDt = dto.ToLocalTime().DateTime;
                return true;
            }
            DateTime dt;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dt))
            {
                localDt = dt.ToLocalTime();
                return true;
            }
            return false;
        }

        private Dictionary<string, object> TryParseDict(string jsonText)
        {
            if (string.IsNullOrWhiteSpace(jsonText)) return null;
            try
            {
                return _json.Deserialize<Dictionary<string, object>>(jsonText);
            }
            catch
            {
                return null;
            }
        }

        private string ExtractErrorMessage(string responseBody)
        {
            Dictionary<string, object> dict = TryParseDict(responseBody);
            if (dict != null)
            {
                string msg = GetStr(dict, "message");
                if (!string.IsNullOrEmpty(msg)) return msg;
                msg = GetStr(dict, "error");
                if (!string.IsNullOrEmpty(msg)) return msg;
                msg = GetStr(dict, "codeDescription");
                if (!string.IsNullOrEmpty(msg)) return msg;
            }
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                string s = responseBody.Trim();
                return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
            }
            return string.Empty;
        }

        private static string GetStr(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return string.Empty;
            return dict[key].ToString();
        }

        private static long GetLong(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return 0;
            object v = dict[key];
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            if (v is decimal) return (long)(decimal)v;
            if (v is double) return (long)(double)v;
            long res;
            if (long.TryParse(v.ToString(), out res)) return res;
            return 0;
        }

        private static double GetDouble(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return 0;
            object v = dict[key];
            if (v is double) return (double)v;
            if (v is decimal) return (double)(decimal)v;
            if (v is int) return (int)v;
            if (v is long) return (long)v;
            double res;
            if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out res)) return res;
            return 0;
        }
    }
}

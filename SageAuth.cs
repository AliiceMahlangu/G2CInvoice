using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;
using OtpNet;

namespace G2C.Invoice
{

    internal static class CredentialStore
    {
        private const int CRED_TYPE_GENERIC = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredFree(IntPtr cred);

        public static (string username, string password) ReadCredential(string target)
        {
            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
                throw new InvalidOperationException(
                    $"Could not read Windows credential '{target}'. Add it via Control Panel > " +
                    $"Credential Manager > Add a generic credential. Win32 error: {Marshal.GetLastWin32Error()}");

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                string password = cred.CredentialBlobSize > 0
                    ? Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2)
                    : null;
                return (cred.UserName, password);
            }
            finally
            {
                CredFree(credPtr);
            }
        }
    }

    internal static class SageAuth
    {

        public const string ClientId = "Client ID";
        public const string ApiKey = "Api key";
        public const string RedirectUri = "https://sage.go2cloud.co.za:8080/callback";
        public const string AuthorizeUrl = "https://id.sage.com/authorize";
        public const string TokenUrl = "https://id.sage.com/oauth/token";

        .
        public const string Scope = "openid profile email offline_access";

        public const string Audience = "sbca-za-prd/PUBLIC-API-RESELLERS";
        public const string RefreshAudience = "https://sage-cid-prod.sageidprod.auth0app.com/userinfo";
        public const string ApiBaseUrl = "https://resellers.accounting.sageone.co.za/api/2.0.0/";
        public static int CompanyId { get; set; } = 17370;
        public static string AccessToken { get; private set; }
        public static string RefreshToken { get; private set; }


        private static readonly string TokenFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "G2CInvoice",
            "token.json");

        private class StoredToken
        {
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
        }

        private static void SaveTokenToDisk()
        {
            try
            {
                var dir = Path.GetDirectoryName(TokenFilePath);
                Directory.CreateDirectory(dir);

                var data = new StoredToken
                {
                    AccessToken = AccessToken,
                    RefreshToken = RefreshToken
                };

                File.WriteAllText(TokenFilePath, JsonSerializer.Serialize(data));
                Console.WriteLine("Token saved.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: could not save token to disk: {ex.Message}");
            }
        }

        private static bool LoadTokenFromDisk()
        {
            try
            {
                if (!File.Exists(TokenFilePath))
                    return false;

                var json = File.ReadAllText(TokenFilePath);
                var data = JsonSerializer.Deserialize<StoredToken>(json);

                if (data == null || string.IsNullOrWhiteSpace(data.RefreshToken))
                    return false;

                AccessToken = data.AccessToken;
                RefreshToken = data.RefreshToken;
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: could not read saved token: {ex.Message}");
                return false;
            }
        }

        private static void ClearSavedToken()
        {
       
            AccessToken = null;
            RefreshToken = null;

            try
            {
                if (File.Exists(TokenFilePath))
                    File.Delete(TokenFilePath);
            }
            catch
            {

            }
        }

       
        public static async Task<bool> EnsureAuthenticatedAsync()
        {
            if (LoadTokenFromDisk())
            {
                Console.WriteLine("Found saved credentials, refreshing access token...");
                var refreshed = await RefreshAccessTokenAsync();
                if (refreshed)
                {
                    SaveTokenToDisk();
                    return true;
                }

                Console.WriteLine("Saved refresh token is no longer valid. Falling back to browser login.");
                ClearSavedToken();
            }

            var authenticated = await AuthenticateInteractivelyAsync();

            if (authenticated)
                SaveTokenToDisk();
            else
                ClearSavedToken();

            return authenticated;
        }

        public static void Validate()
        {
            if (string.IsNullOrWhiteSpace(ClientId) ||
                ClientId.Trim().Equals("client id", StringComparison.OrdinalIgnoreCase) ||
                ClientId.Trim().Equals("clientid", StringComparison.OrdinalIgnoreCase) ||
                ClientId == "YOUR_CLIENT_ID_HERE")
            {
                throw new InvalidOperationException(
                    "ClientId is not set in SageAuth.cs (it still holds a placeholder value).");
            }

            if (string.IsNullOrWhiteSpace(ApiKey) ||
                ApiKey.Trim().Equals("YOUR_API_KEY_HERE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ApiKey is not set in SageAuth.cs (it still holds a placeholder value). " +
                    "This is required as a query string parameter on every Accounting API call.");
            }

            if (string.IsNullOrWhiteSpace(RedirectUri))
                throw new InvalidOperationException("RedirectUri is not set in SageAuth.cs.");

            Console.WriteLine("Sage OAuth config loaded.");
            Console.WriteLine($"Redirect URI: {RedirectUri}");
            Console.WriteLine($"Token endpoint: {TokenUrl}");
            Console.WriteLine($"API base: {ApiBaseUrl}");
        }

              private static void ReportTokenShape()
        {
            if (string.IsNullOrWhiteSpace(AccessToken))
            {
                Console.WriteLine("[token] none");
                return;
            }

            int segments = AccessToken.Split('.').Length;
            string prefix = AccessToken.Substring(0, Math.Min(8, AccessToken.Length));

            Console.WriteLine($"[token] len={AccessToken.Length} segments={segments} starts={prefix}");

            if (segments != 3)
            {
                Console.WriteLine("[token] NOT a JWT - this is an opaque token.");
                Console.WriteLine("[token] Check that the 'audience' parameter is present on the");
                Console.WriteLine("[token] /authorize (and /oauth/token refresh) requests and matches");
                Console.WriteLine("[token] the Accounting API's registered audience value exactly.");
            }
        }


        public static string GenerateCodeVerifier()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return Base64UrlEncode(bytes);
        }

        private static string Base64UrlEncode(byte[] input)
        {
            return Convert.ToBase64String(input)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static string ComputeCodeChallenge(string codeVerifier)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
            return Base64UrlEncode(hash);
        }


        public static string BuildAuthorizationUrl(string codeChallenge, string state)
        {
            var qs =
                $"response_type=code" +
                $"&client_id={Uri.EscapeDataString(ClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                $"&scope={Uri.EscapeDataString(Scope)}" +
                $"&audience={Uri.EscapeDataString(Audience)}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
                $"&code_challenge_method=S256";

            return $"{AuthorizeUrl}?{qs}";
        }


        public static async Task<bool> ExchangeCodeForTokenAsync(string authorizationCode, string codeVerifier)
        {
            using var client = new HttpClient();

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type",    "authorization_code"),
                new KeyValuePair<string, string>("client_id",     ClientId),
                new KeyValuePair<string, string>("code",          authorizationCode),
                new KeyValuePair<string, string>("redirect_uri",  RedirectUri),
                new KeyValuePair<string, string>("code_verifier", codeVerifier)
            });

            var response = await client.PostAsync(TokenUrl, form);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Token exchange failed: {(int)response.StatusCode} {response.StatusCode} - {body}");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            AccessToken = doc.RootElement.GetProperty("access_token").GetString();
            RefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(RefreshToken))
            {
                Console.WriteLine("Warning: no refresh_token was returned. Future runs will require");
                Console.WriteLine("interactive login again. Confirm the 'offline_access' scope is");
                Console.WriteLine("enabled for this client in Sage's app registration.");
            }

            Console.WriteLine("Access token received successfully.");
            ReportTokenShape();
            return true;
        }

        public static async Task<bool> RefreshAccessTokenAsync()
        {
            if (string.IsNullOrWhiteSpace(RefreshToken))
                return false;

            if (await TryRefreshAsync(audience: null))
                return true;

            Console.WriteLine("Refresh without audience failed, retrying with combined audience...");
            return await TryRefreshAsync(audience: $"{Audience},{RefreshAudience}");
        }

        private static async Task<bool> TryRefreshAsync(string audience)
        {
            using var client = new HttpClient();

            var pairs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("grant_type",    "refresh_token"),
                new KeyValuePair<string, string>("client_id",     ClientId),
                new KeyValuePair<string, string>("refresh_token", RefreshToken),
                new KeyValuePair<string, string>("scope",         Scope)
            };

            if (!string.IsNullOrWhiteSpace(audience))
                pairs.Add(new KeyValuePair<string, string>("audience", audience));

            var form = new FormUrlEncodedContent(pairs);

            var response = await client.PostAsync(TokenUrl, form);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Token refresh failed: {(int)response.StatusCode} {response.StatusCode} - {body}");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            AccessToken = doc.RootElement.GetProperty("access_token").GetString();
            RefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString()
                : RefreshToken;

            Console.WriteLine("Access token refreshed.");
            ReportTokenShape();
            return true;
        }


        public static async Task<bool> AuthenticateInteractivelyAsync()
        {
            var verifier = GenerateCodeVerifier();
            var challenge = ComputeCodeChallenge(verifier);
            var state = Guid.NewGuid().ToString("N");
            var authUrl = BuildAuthorizationUrl(challenge, state);

            Console.WriteLine("\n=== One-time login required ===");
            Console.WriteLine("(This should only happen once, or if the saved login expires -");
            Console.WriteLine(" normal monthly runs will not need this step.)");
            Console.WriteLine("\nOpening a browser window to log in to Sage...");
            Console.WriteLine("Log in as normal - the tool will pick up the redirect automatically,");
            Console.WriteLine("no need to copy or paste anything.");

            string redirectedUrl = await TryAutomatedLoginAsync(authUrl);

            if (redirectedUrl == null)
            {
    
                Console.WriteLine("\nFalling back to manual login.");
                Console.WriteLine("If a browser didn't open, visit this URL:");
                Console.WriteLine(authUrl);
                Console.WriteLine();
                Console.WriteLine("After logging in, your browser will redirect to a sage.go2cloud.co.za URL.");
                Console.WriteLine("The page will not load - that is expected. Copy the FULL URL from the");
                Console.WriteLine("address bar anyway and paste it below.");
                Console.Write("Paste URL here: ");
                redirectedUrl = Console.ReadLine();
            }

            if (string.IsNullOrWhiteSpace(redirectedUrl))
            {
                Console.WriteLine("No URL obtained.");
                return false;
            }

            if (!Uri.TryCreate(redirectedUrl.Trim(), UriKind.Absolute, out var uri))
            {
                Console.WriteLine("That doesn't look like a valid URL.");
                return false;
            }

            var queryParams = ParseQueryString(uri.Query);

            if (!queryParams.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                if (queryParams.TryGetValue("error_description", out var desc))
                    Console.WriteLine($"Sage returned an error: {desc}");
                else if (queryParams.TryGetValue("error", out var error))
                    Console.WriteLine($"Sage returned an error: {error}");
                else
                    Console.WriteLine("Could not find 'code' in the redirect URL.");
                return false;
            }

            queryParams.TryGetValue("state", out var returnedState);
            if (!string.Equals(returnedState, state, StringComparison.Ordinal))
            {
                Console.WriteLine("State mismatch - possible CSRF issue, or a redirect from an");
                Console.WriteLine("earlier run. Aborting.");
                return false;
            }

            Console.WriteLine("Authorization code received. Exchanging for tokens...");
            return await ExchangeCodeForTokenAsync(code, verifier);
        }

       
        private static async Task<string> TryAutomatedLoginAsync(string authUrl)
        {
            try
            {
                var (username, password) = CredentialStore.ReadCredential("G2CInvoice_SagePassword");
                var (_, totpSecret) = CredentialStore.ReadCredential("G2CInvoice_SageTotp");

                using var playwright = await Playwright.CreateAsync();

                await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = false,
                    Args = new[] { "--window-position=-32000,-32000" }
                });

                var page = await browser.NewPageAsync();

               
                await page.GotoAsync(authUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 60000
                });

                var emailSelector = "input#username, input[name='username'], input[type='email']";
                await page.WaitForSelectorAsync(emailSelector, new PageWaitForSelectorOptions { Timeout = 30000 });
                await page.FillAsync(emailSelector, username);

                var emailContinue = "button[type='submit']";
                if (await page.IsVisibleAsync(emailContinue))
                    await page.ClickAsync(emailContinue);

                var passwordSelector = "input#password, input[name='password'], input[type='password']";
                await page.WaitForSelectorAsync(passwordSelector, new PageWaitForSelectorOptions { Timeout = 30000 });
                await page.FillAsync(passwordSelector, password);
                await page.ClickAsync("button[type='submit']");

                var codeSelector = "input[name='code'], input#code, input[type='tel'], input[inputmode='numeric']";
                await page.WaitForSelectorAsync(codeSelector, new PageWaitForSelectorOptions { Timeout = 30000 });
                var totpCode = new Totp(Base32Encoding.ToBytes(totpSecret)).ComputeTotp();
                await page.FillAsync(codeSelector, totpCode);

                var mfaContinue = "button[type='submit']";
                if (await page.IsVisibleAsync(mfaContinue))
                    await page.ClickAsync(mfaContinue);

                await page.WaitForURLAsync(
                    url => url.StartsWith(RedirectUri, StringComparison.OrdinalIgnoreCase),
                    new PageWaitForURLOptions { Timeout = 60000 });

                var result = page.Url;
                await browser.CloseAsync();
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Automated browser login unavailable or failed: {ex.Message}");
                return null;
            }
        }


        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(query))
                return result;

            if (query.StartsWith("?"))
                query = query.Substring(1);

            foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split(new[] { '=' }, 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                result[key] = value;
            }

            return result;
        }

        public static HttpClient CreateClient()
        {
            if (string.IsNullOrWhiteSpace(AccessToken))
                throw new InvalidOperationException("No access token - complete the OAuth flow first.");

            var client = new HttpClient
            {
                BaseAddress = new Uri(ApiBaseUrl),
                Timeout = TimeSpan.FromSeconds(60)
            };

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", AccessToken);
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            return client;
        }

        public static string BuildRequestPath(string endpoint, params (string key, string value)[] extraQueryParams)
        {
            var sb = new StringBuilder();
            sb.Append(endpoint.TrimStart('/'));
            sb.Append("?apikey=").Append(Uri.EscapeDataString(ApiKey));

            foreach (var (key, value) in extraQueryParams)
            {
                sb.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value ?? string.Empty));
            }

            return sb.ToString();
        }
    }
}
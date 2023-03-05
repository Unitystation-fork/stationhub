using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Firebase.Auth;
using Serilog;
using UnitystationLauncher.Constants;
using UnitystationLauncher.Models.ConfigFile;

namespace UnitystationLauncher.Services
{
    public class AuthService
    {
        readonly FirebaseAuthProvider _authProvider;
        private readonly HttpClient _http;
        public LoginMsg? LoginMsg { get; set; }
        public bool AttemptingAutoLogin { get; set; }

        public AuthService(HttpClient http, FirebaseAuthProvider authProvider)
        {
            _authProvider = authProvider;
            _http = http;

            LoadAuthSettings();
        }

        public FirebaseAuthLink? AuthLink { get; set; }
        public string? CurrentRefreshToken => AuthLink?.RefreshToken;

        public string? Uid => AuthLink?.User.LocalId;

        private readonly string AuthSettingsPath = Path.Combine(Config.RootFolder, "authSettings.json");

        private void ConvertToNewAuthFileName()
        {
            string oldAuthSettingsPath = Path.Combine(Config.RootFolder, "settings.json");
            if (File.Exists(oldAuthSettingsPath))
            {
                File.Move(oldAuthSettingsPath, AuthSettingsPath);
            }
        }

        private void LoadAuthSettings()
        {
            try
            {
                ConvertToNewAuthFileName();

                if (File.Exists(AuthSettingsPath))
                {
                    var json = File.ReadAllText(AuthSettingsPath);
                    var authLink = JsonSerializer.Deserialize<FirebaseAuthLink>(json);
                    AuthLink = authLink;
                }
            }
            catch (Exception)
            {
                // Something went wrong reading the auth settings. Just ask the user to log in again.
                // The auth settings file will get overwritten after they do so we don't need to clean it up.
            }

        }

        public void SaveAuthSettings()
        {
            var json = JsonSerializer.Serialize(AuthLink);

            using (StreamWriter writer = File.CreateText(AuthSettingsPath))
            {
                writer.WriteLine(json);
            }
        }

        public void ResendVerificationEmail()
        {
            _authProvider.SendEmailVerificationAsync(AuthLink);
        }

        public void SendForgotPasswordEmail(string email)
        {
            _authProvider.SendPasswordResetEmailAsync(email);
        }

        internal Task<FirebaseAuthLink> SignInWithEmailAndPasswordAsync(string email, string password) =>
            _authProvider.SignInWithEmailAndPasswordAsync(email, password);

        internal Task<FirebaseAuthLink> SignInWithCustomTokenAsync(string token) =>
            _authProvider.SignInWithCustomTokenAsync(token);

        /// <summary>
        /// Asks firebase to create the user's account.
        /// The provided email's domain is checked against a list of disposable email addresses.
        /// If the domain is not in the list (or if GitHub is down) then account creation continues.
        /// Otherwise an exception is thrown.
        /// </summary>
        /// <returns></returns>
        internal async Task<FirebaseAuthLink> CreateAccountAsync(string username, string email, string password)
        {
            // Client-side check for disposable email address.
            const string url =
                "https://raw.githubusercontent.com/martenson/disposable-email-domains/master/disposable_email_blocklist.conf";
            HttpRequestMessage requestMessage = new(HttpMethod.Get, url);

            CancellationToken cancellationToken = new CancellationTokenSource(60000).Token;
            bool isDomainBlacklisted = false;
            try
            {
                HttpResponseMessage response = await _http.SendAsync(requestMessage, cancellationToken);
                string msg = await response.Content.ReadAsStringAsync(cancellationToken);

                // Turn msg into a hashset of all domains
                using StringReader stringReader = new(msg);
                List<string> lines = new();

                while (await stringReader.ReadLineAsync() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("//"))
                    {
                        lines.Add(line);
                    }
                }

                HashSet<string> blacklist = new(lines, StringComparer.OrdinalIgnoreCase);

                MailAddress address = new(email);
                if (blacklist.Contains(address.Host))
                {
                    // Randomly wait before failing. Might frustrate users who try different disposable emails.
                    await Task.Delay(new Random().Next(3000, 12000), cancellationToken);
                    isDomainBlacklisted = true;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Error or timeout in check for email domain blacklist, check has been skipped");
            }

            if (isDomainBlacklisted)
            {
                throw new InvalidOperationException("The email domain provided by the user is on our blacklist.");
            }

            return await _authProvider.CreateUserWithEmailAndPasswordAsync(email, password, username, true);
        }

        internal Task<User> GetUpdatedUserAsync() => _authProvider.GetUserAsync(AuthLink);

        public async Task<string> GetCustomTokenAsync(RefreshToken refreshToken)
        {
            HttpRequestMessage r = new(HttpMethod.Get, ApiUrls.ValidateTokenUrl + Uri.EscapeDataString(JsonSerializer.Serialize(refreshToken)));
            CancellationToken cancellationToken = new CancellationTokenSource(120000).Token;
            HttpResponseMessage res;

            try
            {
                res = await _http.SendAsync(r, cancellationToken);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed when sending token validation request");
                return "";
            }

            string msg = await res.Content.ReadAsStringAsync(cancellationToken);
            ApiResponse? response = JsonSerializer.Deserialize<ApiResponse>(msg, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (response == null)
            {
                Log.Error("Error: {Error}", "Response from /validatetoken cannot be deserialized");
                return "";
            }

            if (response.ErrorCode != 0)
            {
                Log.Error("Error: {Error}", response.ErrorMsg);
                return "";
            }

            return response.Message ?? "";
        }

        public async Task SignOutUserAsync()
        {
            if (AuthLink == null || Uid == null || CurrentRefreshToken == null)
            {
                return;
            }

            RefreshToken token = new()
            {
                UserId = Uid,
                Token = CurrentRefreshToken
            };

            HttpRequestMessage r = new(HttpMethod.Get, ApiUrls.SignOutUrl + Uri.EscapeDataString(JsonSerializer.Serialize(token)));
            CancellationToken cancellationToken = new CancellationTokenSource(120000).Token;
            HttpResponseMessage res;

            try
            {
                res = await _http.SendAsync(r, cancellationToken);
            }
            catch (Exception e)
            {
                Log.Error(e, "Http request to sign out failed");
                return;
            }

            string msg = await res.Content.ReadAsStringAsync(cancellationToken);

            Log.Information("Logout message: {Message}", msg);
            AuthLink = null;
        }
    }



    public class LoginMsg
    {
        public string Email { get; set; } = "";
        public string Pass { get; set; } = "";
    }

    [Serializable]
    public class RefreshToken
    {
        [JsonPropertyName("RefreshToken")] public string? Token { get; set; }
        public string? UserId { get; set; }
    }

    [Serializable]
    public class ApiResponse
    {
        /// <summary>
        /// 0 = all good, read the message variable now, otherwise read errorMsg
        /// </summary>
        public int ErrorCode { get; set; }

        public string? ErrorMsg { get; set; }
        public string? Message { get; set; }
    }
}
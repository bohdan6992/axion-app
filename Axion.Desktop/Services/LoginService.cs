using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Axion.Desktop.Services
{
    public sealed class LoginService
    {
        private readonly HttpClient _http;
        public string? Jwt { get; private set; }
        public string? Email { get; private set; }

        public LoginService(string baseUrl)
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl),
                Timeout = TimeSpan.FromSeconds(15),
            };
        }

        public async Task<bool> LoginAsync(string email, string password, CancellationToken ct)
        {
            // контракт: POST /api/auth/login { email, password } -> { token }
            var req = new LoginRequest { Email = email, Password = password };

            using var resp = await _http.PostAsJsonAsync("/api/auth/login", req, ct);
            if (!resp.IsSuccessStatusCode)
                return false;

            var dto = await resp.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken: ct);
            if (dto?.Token is null || dto.Token.Length < 20)
                return false;

            Email = email;
            Jwt = dto.Token;
            return true;
        }

        public HttpClient CreateAuthedClient(string baseUrl)
        {
            var c = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
            if (!string.IsNullOrWhiteSpace(Jwt))
                c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Jwt);
            return c;
        }

        private sealed class LoginRequest
        {
            public string Email { get; set; } = "";
            public string Password { get; set; } = "";
        }

        private sealed class LoginResponse
        {
            public string Token { get; set; } = "";
        }
    }
}

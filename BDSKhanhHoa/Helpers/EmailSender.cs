using System.Text;
using Newtonsoft.Json;

namespace BDSKhanhHoa.Helpers
{
    public class EmailSender
    {
        private readonly IConfiguration _config;
        private readonly HttpClient _httpClient;

        public EmailSender(IConfiguration config)
        {
            _config = config;
            _httpClient = new HttpClient();
        }

        public async Task SendEmailAsync(string toEmail, string subject, string htmlContent)
        {
            // Lấy API Key từ trường Password trong config Brevo của bạn (đó chính là API Key v3)
            var apiKey = _config["BrevoSettings:Password"];
            var senderEmail = _config["BrevoSettings:SenderEmail"];
            var senderName = _config["BrevoSettings:SenderName"];

            var emailData = new
            {
                sender = new { name = senderName, email = senderEmail },
                to = new[] { new { email = toEmail } },
                subject = subject,
                htmlContent = htmlContent
            };

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
            request.Headers.Add("api-key", apiKey);
            request.Content = new StringContent(JsonConvert.SerializeObject(emailData), Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[OK] Đã gửi OTP thành công tới {toEmail} qua Brevo API");
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"[LỖI BREVO API] {response.StatusCode}: {errorBody}");
                    throw new Exception("Không thể gửi email qua Brevo API.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SYSTEM ERROR] {ex.Message}");
                throw;
            }
        }
    }
}
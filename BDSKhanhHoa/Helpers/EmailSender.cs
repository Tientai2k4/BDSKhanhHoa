using SendGrid;
using SendGrid.Helpers.Mail;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace BDSKhanhHoa.Helpers
{
    public class EmailSender
    {
        private readonly IConfiguration _config;
        private readonly ILogger<EmailSender> _logger;

        public EmailSender(IConfiguration config, ILogger<EmailSender> logger)
        {
            _config = config;
            _logger = logger;
        }

        public async Task<bool> SendEmailAsync(string toEmail, string subject, string htmlContent)
        {
            try
            {
                var apiKey = _config["SendGridSettings:ApiKey"];
                var client = new SendGridClient(apiKey);

                var fromEmail = _config["SendGridSettings:SenderEmail"];
                var fromName = _config["SendGridSettings:SenderName"];

                var from = new EmailAddress(fromEmail, fromName);
                var to = new EmailAddress(toEmail);

                // Tạo nội dung text thuần từ HTML (để tránh bị coi là Spam)
                var plainTextContent = "Mã xác thực của bạn từ BDS Khánh Hòa";

                var msg = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent, htmlContent);

                var response = await client.SendEmailAsync(msg);

                if (response.StatusCode == HttpStatusCode.Accepted || response.StatusCode == HttpStatusCode.OK)
                {
                    _logger.LogInformation($"Email sent successfully to {toEmail}");
                    return true;
                }
                else
                {
                    var errorBody = await response.Body.ReadAsStringAsync();
                    _logger.LogError($"Failed to send email. Status: {response.StatusCode}. Error: {errorBody}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception while sending email: {ex.Message}");
                return false;
            }
        }
    }
}
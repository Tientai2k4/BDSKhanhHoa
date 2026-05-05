using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Services
{
    public class SendGridEmailService : IEmailService
    {
        private readonly IConfiguration _configuration;

        public SendGridEmailService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task SendEmailAsync(string toEmail, string subject, string htmlMessage)
        {
            // Đọc thông tin từ appsettings.json bạn vừa cung cấp
            var apiKey = _configuration["SendGridSettings:ApiKey"];
            var senderEmail = _configuration["SendGridSettings:SenderEmail"];
            var senderName = _configuration["SendGridSettings:SenderName"];

            var client = new SendGridClient(apiKey);
            var from = new EmailAddress(senderEmail, senderName);
            var to = new EmailAddress(toEmail);

            // Tham số thứ 4 là plainTextContent (để trống vì ta dùng HTML), tham số thứ 5 là htmlContent
            var msg = MailHelper.CreateSingleEmail(from, to, subject, string.Empty, htmlMessage);

            // Gửi email
            await client.SendEmailAsync(msg);
        }
    }
}
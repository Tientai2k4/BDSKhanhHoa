using System.Net;
using System.Net.Mail;
using BDSKhanhHoa.Services;

namespace BDSKhanhHoa.Helpers
{
    public class EmailSender : IEmailService
    {
        private readonly IConfiguration _config;

        public EmailSender(IConfiguration config)
        {
            _config = config;
        }

        public async Task SendEmailAsync(
            string toEmail,
            string subject,
            string htmlContent)
        {
            var smtpServer = _config["BrevoSettings:SmtpServer"];
            var port = int.Parse(_config["BrevoSettings:Port"]);
            var username = _config["BrevoSettings:Username"];
            var password = _config["BrevoSettings:Password"];
            var senderEmail = _config["BrevoSettings:SenderEmail"];
            var senderName = _config["BrevoSettings:SenderName"];

            using (var client = new SmtpClient(smtpServer, port))
            {
                client.EnableSsl = true;

                client.UseDefaultCredentials = false;

                client.Credentials = new NetworkCredential(
                    username,
                    password
                );

                client.DeliveryMethod = SmtpDeliveryMethod.Network;

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(senderEmail, senderName),
                    Subject = subject,
                    Body = htmlContent,
                    IsBodyHtml = true
                };

                mailMessage.To.Add(toEmail);

                try
                {
                    await client.SendMailAsync(mailMessage);

                    Console.WriteLine(
                        $"[BREVO SMTP SUCCESS] Đã gửi mail tới {toEmail}"
                    );
                }
                catch (SmtpException smtpEx)
                {
                    Console.WriteLine(
                        $"[BREVO SMTP ERROR] {smtpEx.StatusCode} - {smtpEx.Message}"
                    );

                    throw;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[BREVO ERROR] {ex.Message}"
                    );

                    throw;
                }
            }
        }
    }
}
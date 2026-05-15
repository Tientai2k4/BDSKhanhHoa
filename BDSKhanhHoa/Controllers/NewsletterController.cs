using BDSKhanhHoa.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.Mail;

namespace BDSKhanhHoa.Controllers
{
    public class NewsletterController : Controller
    {
        private readonly IEmailService _emailService;
        private readonly ILogger<NewsletterController> _logger;

        public NewsletterController(
            IEmailService emailService,
            ILogger<NewsletterController> logger)
        {
            _emailService = emailService;
            _logger = logger;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Subscribe(string email, string? returnUrl)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                TempData["NewsletterError"] = "Vui lòng nhập email.";
                return RedirectBack(returnUrl);
            }

            email = email.Trim();

            if (!IsValidEmail(email))
            {
                TempData["NewsletterError"] = "Email không hợp lệ. Vui lòng kiểm tra lại.";
                return RedirectBack(returnUrl);
            }

            try
            {
                string subject = "Đăng ký nhận tin BĐS Khánh Hòa thành công";

                string htmlContent = $@"
                    <div style='font-family:Arial,sans-serif;line-height:1.7;color:#0f172a;'>
                        <h2 style='color:#2563eb;'>BĐS Khánh Hòa</h2>
                        <p>Xin chào,</p>
                        <p>Bạn đã đăng ký nhận tin bất động sản mới từ hệ thống <strong>BĐS Khánh Hòa</strong>.</p>
                        <p>Chúng tôi sẽ gửi đến bạn các thông tin mới về:</p>
                        <ul>
                            <li>Tin mua bán bất động sản nổi bật</li>
                            <li>Tin cho thuê mới cập nhật</li>
                            <li>Dự án bất động sản tại Khánh Hòa</li>
                            <li>Tin tức thị trường và cơ hội đầu tư</li>
                        </ul>
                        <p>Cảm ơn bạn đã quan tâm và đồng hành cùng BĐS Khánh Hòa.</p>
                        <hr>
                        <p style='font-size:13px;color:#64748b;'>
                            Email này được gửi tự động từ hệ thống BĐS Khánh Hòa.
                        </p>
                    </div>";

                await _emailService.SendEmailAsync(email, subject, htmlContent);

                TempData["NewsletterSuccess"] = "Đăng ký thành công! Hệ thống đã gửi email xác nhận cho bạn.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi gửi email newsletter tới {Email}", email);
                TempData["NewsletterError"] = "Không thể gửi email lúc này. Vui lòng thử lại sau.";
            }

            return RedirectBack(returnUrl);
        }

        private IActionResult RedirectBack(string? returnUrl)
        {
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);

            return RedirectToAction("Index", "Home");
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var mail = new MailAddress(email);
                return mail.Address == email;
            }
            catch
            {
                return false;
            }
        }
    }
}
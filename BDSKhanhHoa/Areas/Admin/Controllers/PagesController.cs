using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize]
    [Route("Admin/[controller]/[action]")]
    public class PagesController : Controller
    {
        [HttpGet]
        public IActionResult Index()
        {
            ViewBag.Pages = new List<(string Key, string Title, string Url, string Description)>
            {
                ("home", "Trang chủ", "/", "Trang public chính của hệ thống"),
                ("projects", "Danh sách dự án", "/Project", "Trang tổng hợp dự án BĐS"),
                ("properties", "Danh sách tin rao", "/Properties", "Trang tổng hợp tin đăng"),
                ("contact", "Liên hệ", "/Contact", "Trang liên hệ hỗ trợ"),
                ("privacy", "Chính sách bảo mật", "/Home/Privacy", "Trang chính sách / điều khoản"),
                ("faq", "Câu hỏi thường gặp", "/Home/FAQ", "Trang FAQ nếu bạn đã có"),
            };

            return View();
        }
    }
}
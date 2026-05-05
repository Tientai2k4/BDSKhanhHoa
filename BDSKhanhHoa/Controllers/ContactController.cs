using Microsoft.AspNetCore.Mvc;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using System.Threading.Tasks;
using System;

namespace BDSKhanhHoa.Controllers
{
    [AllowAnonymous] // Công khai hoàn toàn
    public class ContactController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ContactController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public IActionResult Index()
        {
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(ContactMessage model)
        {
            if (ModelState.IsValid)
            {
                // Mặc định các giá trị khi khách vãng lai gửi
                model.Status = "Chưa xử lý";
                model.CreatedAt = DateTime.Now;
                model.UserID = null; // Khách vãng lai không có UserID
                model.ProjectID = null; // Liên hệ chung, không gắn với dự án cụ thể

                _context.ContactMessages.Add(model);
                await _context.SaveChangesAsync();

                TempData["SuccessMessage"] = "Cảm ơn bạn đã liên hệ! Đội ngũ tư vấn sẽ phản hồi lại bạn trong thời gian sớm nhất.";
                return RedirectToAction("Index");
            }

            TempData["ErrorMessage"] = "Vui lòng điền đầy đủ các thông tin bắt buộc.";
            return View(model);
        }
    }
}
using Microsoft.AspNetCore.Mvc;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Controllers
{
    public class ContactController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ContactController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public IActionResult Index() => View();

        [HttpPost]
        public async Task<IActionResult> Index(ContactMessage model)
        {
            if (ModelState.IsValid)
            {
                // Thêm vào DB siêu đơn giản với EF Core
                _context.ContactMessages.Add(model);
                await _context.SaveChangesAsync(); // Lưu thay đổi

                TempData["SuccessMessage"] = "Gửi thành công!";
                return RedirectToAction("Index");
            }
            return View(model);
        }
    }
}
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using BDSKhanhHoa.Data;
using System.Threading.Tasks;
using System.Linq;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    public class ContactController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ContactController(ApplicationDbContext context)
        {
            _context = context;
        }

        // Lấy tất cả (Thay cho GetAllContactsAsync)
        public async Task<IActionResult> Index()
        {
            var contacts = await _context.ContactMessages
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();
            return View(contacts);
        }

        // Lấy chi tiết (Thay cho GetContactByIdAsync)
        public async Task<IActionResult> Details(int id)
        {
            var contact = await _context.ContactMessages.FindAsync(id);
            if (contact == null) return NotFound();
            return View(contact);
        }

        // Cập nhật trạng thái (Thay cho UpdateContactStatusAsync)
        [HttpPost]
        public async Task<IActionResult> UpdateStatus(int id, string status)
        {
            var contact = await _context.ContactMessages.FindAsync(id);
            if (contact != null)
            {
                contact.Status = status;
                await _context.SaveChangesAsync(); // Tự động tạo mã UPDATE SQL
            }
            TempData["SuccessMsg"] = "Đã cập nhật!";
            return RedirectToAction(nameof(Details), new { id = id });
        }

        // Xóa (Thay cho DeleteContactAsync)
        [HttpPost]
        public async Task<IActionResult> Delete(int id)
        {
            var contact = await _context.ContactMessages.FindAsync(id);
            if (contact != null)
            {
                _context.ContactMessages.Remove(contact);
                await _context.SaveChangesAsync(); // Tự động tạo mã DELETE SQL
            }
            return RedirectToAction(nameof(Index));
        }
    }
}
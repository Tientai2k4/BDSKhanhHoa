using Microsoft.AspNetCore.Mvc;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")] // Cho phép Admin và Staff quản lý nội dung AI
    public class AISettingsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AISettingsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public IActionResult Index()
        {
            // 1. FIX LỖI "Data is Null": Chạy lệnh SQL tự động chuyển các cột NULL thành chuỗi rỗng 
            // Giúp EF Core không bị crash khi map dữ liệu từ Database vào C# Model
            _context.Database.ExecuteSqlRaw("UPDATE [StaticPages] SET [Description] = '' WHERE [Description] IS NULL");
            _context.Database.ExecuteSqlRaw("UPDATE [StaticPages] SET [Content] = '' WHERE [Content] IS NULL");

            // 2. Tận dụng bảng StaticPages, dùng PageKey "ai_knowledge_base" để lưu RAG data
            var aiKnowledge = _context.StaticPages.FirstOrDefault(s => s.PageKey == "ai_knowledge_base");

            // 3. Nếu chưa có, tự động tạo mới một bản ghi mặc định (Ép các trường không được NULL)
            if (aiKnowledge == null)
            {
                aiKnowledge = new StaticPage
                {
                    PageKey = "ai_knowledge_base",
                    Title = "Dữ liệu Huấn luyện AI (RAG)",
                    Description = "", // Fix lỗi Null
                    Content = "Nhập các quy định pháp lý, chính sách vay vốn, hoặc thông tin quy hoạch dự án Khánh Hòa vào đây để AI học...",
                    UpdatedAt = DateTime.Now
                };
                _context.StaticPages.Add(aiKnowledge);
                _context.SaveChanges();
            }

            return View(aiKnowledge);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Save(StaticPage model)
        {
            var aiKnowledge = _context.StaticPages.FirstOrDefault(s => s.PageKey == "ai_knowledge_base");
            if (aiKnowledge != null)
            {
                // Ép chuỗi rỗng nếu nội dung người dùng lưu là khoảng trắng, tránh lỗi Null
                aiKnowledge.Content = model.Content ?? "";
                aiKnowledge.UpdatedAt = DateTime.Now;
                _context.SaveChanges();

                TempData["Success"] = "Đã cập nhật nguồn kiến thức cho Chatbot AI thành công!";
            }
            return RedirectToAction("Index");
        }
    }
}
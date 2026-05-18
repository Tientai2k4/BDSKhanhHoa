using Microsoft.AspNetCore.Mvc;
using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class AISettingsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public AISettingsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> Index()
        {
            await FixStaticPageNullDataAsync();

            StaticPage? aiKnowledge = await _context.StaticPages
                .FirstOrDefaultAsync(s => s.PageKey == "ai_knowledge_base");

            if (aiKnowledge == null)
            {
                aiKnowledge = new StaticPage
                {
                    PageKey = "ai_knowledge_base",
                    Title = "Dữ liệu Huấn luyện AI (RAG)",
                    Description = "Nguồn dữ liệu nội bộ giúp chatbot trả lời chính sách, quy định, pháp lý, gói VIP và nghiệp vụ sàn.",
                    Content = "",
                    UpdatedAt = DateTime.Now
                };

                _context.StaticPages.Add(aiKnowledge);
                await _context.SaveChangesAsync();
            }

            return View(aiKnowledge);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(StaticPage model)
        {
            StaticPage? aiKnowledge = await _context.StaticPages
                .FirstOrDefaultAsync(s => s.PageKey == "ai_knowledge_base");

            if (aiKnowledge == null)
            {
                aiKnowledge = new StaticPage
                {
                    PageKey = "ai_knowledge_base",
                    Title = "Dữ liệu Huấn luyện AI (RAG)",
                    Description = "Nguồn dữ liệu nội bộ giúp chatbot trả lời chính xác hơn.",
                    Content = model.Content ?? "",
                    UpdatedAt = DateTime.Now
                };

                _context.StaticPages.Add(aiKnowledge);
            }
            else
            {
                aiKnowledge.Content = model.Content ?? "";
                aiKnowledge.Description ??= "";
                aiKnowledge.UpdatedAt = DateTime.Now;
            }

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã cập nhật nguồn kiến thức cho Chatbot AI thành công.";

            return RedirectToAction(nameof(Index));
        }

        private async Task FixStaticPageNullDataAsync()
        {
            await _context.Database.ExecuteSqlRawAsync(
                "UPDATE [StaticPages] SET [Description] = '' WHERE [Description] IS NULL");

            await _context.Database.ExecuteSqlRawAsync(
                "UPDATE [StaticPages] SET [Content] = '' WHERE [Content] IS NULL");
        }
    }
}
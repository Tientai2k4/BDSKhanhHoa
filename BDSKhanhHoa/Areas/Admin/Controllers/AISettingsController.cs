using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

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
        public async Task<IActionResult> Index(string? category, string? keyword, string? status)
        {
            IQueryable<AIKnowledgeArticle> baseQuery = _context.AIKnowledgeArticles.AsNoTracking();

            int totalAll = await baseQuery.CountAsync();
            int publishedAll = await baseQuery.CountAsync(x => x.IsPublished);
            int hiddenAll = totalAll - publishedAll;

            Dictionary<string, int> categoryCounts = await baseQuery
                .GroupBy(x => x.Category)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Category, x => x.Count, StringComparer.OrdinalIgnoreCase);

            IQueryable<AIKnowledgeArticle> query = baseQuery;

            if (!string.IsNullOrWhiteSpace(category))
            {
                string selectedCategory = category.Trim();
                query = query.Where(x => x.Category == selectedCategory);
            }

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string kw = keyword.Trim();
                query = query.Where(x =>
                    x.Title.Contains(kw) ||
                    x.Content.Contains(kw) ||
                    x.Category.Contains(kw));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (string.Equals(status, "published", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(x => x.IsPublished);
                }
                else if (string.Equals(status, "hidden", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(x => !x.IsPublished);
                }
            }

            List<AIKnowledgeArticle> articles = await query
                .OrderBy(x => x.Category)
                .ThenBy(x => x.Title)
                .ToListAsync();

            ViewBag.Category = category ?? string.Empty;
            ViewBag.Keyword = keyword ?? string.Empty;
            ViewBag.Status = status ?? string.Empty;
            ViewBag.TotalAll = totalAll;
            ViewBag.PublishedAll = publishedAll;
            ViewBag.HiddenAll = hiddenAll;
            ViewBag.CategoryCounts = categoryCounts;
            ViewBag.CategoryOptions = GetCategoryOptions();

            return View(articles);
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int? id)
        {
            ViewBag.CategoryOptions = GetCategoryOptions();

            if (id == null || id.Value <= 0)
            {
                return View(new AIKnowledgeArticle
                {
                    Title = string.Empty,
                    Category = "General",
                    Content = string.Empty,
                    IsPublished = true,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                });
            }

            AIKnowledgeArticle? article = await _context.AIKnowledgeArticles
                .FirstOrDefaultAsync(x => x.ArticleID == id.Value);

            if (article == null)
            {
                return NotFound();
            }

            return View(article);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(AIKnowledgeArticle model)
        {
            ViewBag.CategoryOptions = GetCategoryOptions();

            if (string.IsNullOrWhiteSpace(model.Title))
            {
                TempData["Error"] = "Tiêu đề không được để trống.";
                return RedirectToAction(nameof(Edit), new { id = model.ArticleID });
            }

            if (string.IsNullOrWhiteSpace(model.Content))
            {
                TempData["Error"] = "Nội dung huấn luyện không được để trống.";
                return RedirectToAction(nameof(Edit), new { id = model.ArticleID });
            }

            string title = model.Title.Trim();
            string category = string.IsNullOrWhiteSpace(model.Category)
                ? "General"
                : model.Category.Trim();

            string content = NormalizeTrainingContent(model.Content);

            AIKnowledgeArticle? article = null;

            if (model.ArticleID > 0)
            {
                article = await _context.AIKnowledgeArticles
                    .FirstOrDefaultAsync(x => x.ArticleID == model.ArticleID);
            }

            if (article == null)
            {
                article = new AIKnowledgeArticle
                {
                    CreatedAt = DateTime.Now
                };

                _context.AIKnowledgeArticles.Add(article);
            }

            article.Title = title;
            article.Category = category;
            article.Content = content;
            article.IsPublished = model.IsPublished;
            article.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã lưu dữ liệu huấn luyện AI thành công.";
            return RedirectToAction(nameof(Index), new { category = article.Category });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TogglePublished(int id)
        {
            AIKnowledgeArticle? article = await _context.AIKnowledgeArticles
                .FirstOrDefaultAsync(x => x.ArticleID == id);

            if (article == null)
            {
                return NotFound();
            }

            article.IsPublished = !article.IsPublished;
            article.UpdatedAt = DateTime.Now;

            await _context.SaveChangesAsync();

            TempData["Success"] = article.IsPublished
                ? "Đã bật dữ liệu huấn luyện."
                : "Đã tạm tắt dữ liệu huấn luyện.";

            return RedirectToAction(nameof(Index), new { category = article.Category });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            AIKnowledgeArticle? article = await _context.AIKnowledgeArticles
                .FirstOrDefaultAsync(x => x.ArticleID == id);

            if (article == null)
            {
                return NotFound();
            }

            string category = article.Category;

            _context.AIKnowledgeArticles.Remove(article);
            await _context.SaveChangesAsync();

            TempData["Success"] = "Đã xóa dữ liệu huấn luyện AI.";
            return RedirectToAction(nameof(Index), new { category });
        }

        private static string NormalizeTrainingContent(string content)
        {
            string normalized = content.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
            normalized = Regex.Replace(normalized, "\n{4,}", "\n\n\n");
            return normalized;
        }

        private static List<KeyValuePair<string, string>> GetCategoryOptions()
        {
            return new List<KeyValuePair<string, string>>
            {
                new("Core", "Vai trò & nguyên tắc"),
                new("Buy", "Tư vấn mua"),
                new("Rent", "Tư vấn thuê"),
                new("PropertyDetail", "Phân tích tin đang xem"),
                new("Legal", "Pháp lý cơ bản"),
                new("Transaction", "Giao dịch & công chứng"),
                new("Loan", "Vay vốn"),
                new("Posting", "Đăng tin"),
                new("Project", "Dự án"),
                new("Care", "Chăm sóc khách hàng"),
                new("Market", "Kinh nghiệm thị trường"),
                new("Search", "Quy tắc tìm tin SQL"),
                new("Guardrail", "Giới hạn an toàn"),
                new("Fallback", "Fallback khi thiếu dữ liệu"),
                new("General", "Thông tin chung")
            };
        }
    }
}

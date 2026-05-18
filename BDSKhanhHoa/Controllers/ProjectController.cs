using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace BDSKhanhHoa.Controllers
{
    public class ProjectController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ProjectController(ApplicationDbContext context)
        {
            _context = context;
        }

        private async Task LoadProjectFiltersAsync()
        {
            ViewBag.Areas = await _context.Areas
                .AsNoTracking()
                .OrderBy(a => a.AreaName)
                .ToListAsync();
        }

        private IQueryable<Project> BuildBaseQuery()
        {
            return _context.Projects
                .AsNoTracking()
                .Include(p => p.Area)
                .Include(p => p.Ward)
                .Include(p => p.Owner)
                .Where(p => p.ApprovalStatus == "Approved" && p.IsDeleted == false);
        }

        [HttpGet]
        [Route("Project")]
        public async Task<IActionResult> Index()
        {
            await LoadProjectFiltersAsync();

            var projects = await BuildBaseQuery()
                .OrderByDescending(p => p.PublishedAt)
                .Take(12)
                .ToListAsync();

            ViewData["Title"] = "Dự án Bất động sản tại Khánh Hòa";
            ViewBag.Keyword = "";
            ViewBag.AreaId = null;
            ViewBag.Status = "";
            ViewBag.Sort = "newest";
            ViewBag.TotalResults = await BuildBaseQuery().CountAsync();

            return View("Search", projects);
        }

        [HttpGet]
        [Route("Project/Search")]
        public async Task<IActionResult> Search(
            string? keyword,
            int? areaId,
            string? status,
            string? sort = "newest",
            int page = 1)
        {
            await LoadProjectFiltersAsync();

            const int pageSize = 12;

            var query = BuildBaseQuery();

            if (!string.IsNullOrWhiteSpace(keyword))
            {
                keyword = keyword.Trim();

                query = query.Where(p =>
                    (p.ProjectName != null && EF.Functions.Like(p.ProjectName, $"%{keyword}%")) ||
                    (p.Investor != null && EF.Functions.Like(p.Investor, $"%{keyword}%")) ||
                    (p.AddressDetail != null && EF.Functions.Like(p.AddressDetail, $"%{keyword}%")) ||
                    (p.Scale != null && EF.Functions.Like(p.Scale, $"%{keyword}%")) ||
                    (p.ProjectType != null && EF.Functions.Like(p.ProjectType, $"%{keyword}%")));
            }

            if (areaId.HasValue && areaId.Value > 0)
            {
                query = query.Where(p => p.AreaID == areaId.Value);
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                query = query.Where(p => p.ProjectStatus == status);
            }

            query = (sort ?? "newest").Trim().ToLowerInvariant() switch
            {
                "price_asc" => query.OrderBy(p => p.PriceMin ?? decimal.MaxValue),
                "price_desc" => query.OrderByDescending(p => p.PriceMax ?? 0),
                "area_asc" => query.OrderBy(p => p.AreaMin ?? double.MaxValue),
                "area_desc" => query.OrderByDescending(p => p.AreaMax ?? 0),
                "popular" => query.OrderByDescending(p => p.Views).ThenByDescending(p => p.PublishedAt),
                "name" => query.OrderBy(p => p.ProjectName),
                _ => query.OrderByDescending(p => p.PublishedAt)
            };

            int totalResults = await query.CountAsync();

            int totalPages = (int)Math.Ceiling(totalResults / (double)pageSize);

            if (totalPages < 1)
            {
                totalPages = 1;
            }

            if (page < 1)
            {
                page = 1;
            }

            if (page > totalPages)
            {
                page = totalPages;
            }

            var results = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            ViewBag.Keyword = keyword;
            ViewBag.AreaId = areaId;
            ViewBag.Status = status;
            ViewBag.Sort = sort;
            ViewBag.TotalResults = totalResults;
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;

            ViewData["Title"] = "Tìm kiếm dự án BĐS";

            return View(results);
        }

        [HttpGet]
        [Route("Project/Details/{id:int}")]
        public async Task<IActionResult> Details(int id)
        {
            int currentUserId = 0;
            string? userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (!string.IsNullOrWhiteSpace(userIdClaim))
            {
                int.TryParse(userIdClaim, out currentUserId);
            }

            bool isAdminOrStaff = User.IsInRole("Admin") || User.IsInRole("Staff");

            var projectCheck = await _context.Projects
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ProjectID == id && p.IsDeleted == false);

            if (projectCheck == null)
            {
                TempData["Error"] = "Dự án không tồn tại, đã bị xóa hoặc đường dẫn không còn hợp lệ.";
                return RedirectToAction("Search", "Project");
            }

            bool isApproved =
                projectCheck.ApprovalStatus == "Approved" ||
                projectCheck.ApprovalStatus == "Đã duyệt";

            bool isProjectOwner =
                currentUserId > 0 &&
                projectCheck.OwnerUserID == currentUserId;

            bool canPreviewProject = isApproved || isProjectOwner || isAdminOrStaff;

            if (!canPreviewProject)
            {
                TempData["Error"] = "Dự án này chưa được phê duyệt công khai nên bạn chưa thể xem chi tiết.";
                return RedirectToAction("Search", "Project");
            }

            if (isApproved)
            {
                await IncreaseProjectViewAsync(id);
            }

            var project = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Include(p => p.Area)
                .Include(p => p.Owner)
                .FirstOrDefaultAsync(p => p.ProjectID == id && p.IsDeleted == false);

            if (project == null)
            {
                TempData["Error"] = "Không tìm thấy dữ liệu dự án.";
                return RedirectToAction("Search", "Project");
            }

            var relatedProjects = await _context.Projects
                .AsNoTracking()
                .Include(p => p.Ward)
                .Include(p => p.Area)
                .Where(p =>
                    p.ProjectID != id &&
                    p.IsDeleted == false &&
                    (
                        p.ApprovalStatus == "Approved" ||
                        p.ApprovalStatus == "Đã duyệt"
                    ) &&
                    p.AreaID == project.AreaID)
                .OrderByDescending(p => p.Views)
                .ThenByDescending(p => p.PublishedAt)
                .Take(3)
                .ToListAsync();

            int leadCount = await _context.ProjectLeads
                .AsNoTracking()
                .CountAsync(l => l.ProjectID == id);

            int propertyViews = await _context.Properties
                .AsNoTracking()
                .Where(x => x.ProjectID == id && x.IsDeleted != true)
                .SumAsync(x => (int?)x.Views) ?? 0;

            int totalProperties = await _context.Properties
                .AsNoTracking()
                .CountAsync(x => x.ProjectID == id && x.IsDeleted != true);

            ViewBag.RelatedProjects = relatedProjects;
            ViewBag.LeadCount = leadCount;
            ViewBag.ProjectViews = project.Views;
            ViewBag.PropertyViews = propertyViews;
            ViewBag.TotalViews = project.Views + propertyViews;
            ViewBag.TotalProperties = totalProperties;
            ViewBag.CanPreviewProject = canPreviewProject;
            ViewBag.IsApprovedProject = isApproved;

            ViewData["Title"] = project.ProjectName + " - Tổng quan & Bảng giá";

            return View(project);
        }
        private async Task IncreaseProjectViewAsync(int projectId)
        {
            await _context.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE dbo.Projects
                SET Views = ISNULL(Views, 0) + 1
                WHERE ProjectID = {projectId}
                  AND IsDeleted = 0
                  AND ApprovalStatus = N'Approved'
            ");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("Project/SubmitLead")]
        public async Task<IActionResult> SubmitLead(ProjectLead model)
        {
            if (model == null || model.ProjectID <= 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Thiếu thông tin dự án."
                });
            }

            if (string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.Phone))
            {
                return Json(new
                {
                    success = false,
                    message = "Vui lòng nhập họ tên và số điện thoại hợp lệ."
                });
            }

            var project = await _context.Projects
                .FirstOrDefaultAsync(p =>
                    p.ProjectID == model.ProjectID &&
                    p.IsDeleted == false &&
                    p.ApprovalStatus == "Approved");

            if (project == null)
            {
                return Json(new
                {
                    success = false,
                    message = "Dự án không tồn tại hoặc chưa được công khai."
                });
            }

            model.Name = model.Name.Trim();
            model.Phone = model.Phone.Trim();
            model.Email = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email.Trim();
            model.Message = string.IsNullOrWhiteSpace(model.Message) ? null : model.Message.Trim();
            model.CreatedAt = DateTime.Now;
            model.LeadStatus = "New";
            model.Note = null;

            _context.ProjectLeads.Add(model);

            if (project.OwnerUserID > 0)
            {
                _context.Notifications.Add(new Notification
                {
                    UserID = project.OwnerUserID,
                    Title = "Có khách hàng quan tâm dự án",
                    Content = $"Có khách hàng vừa gửi yêu cầu tư vấn cho dự án \"{project.ProjectName}\".",
                    ActionUrl = $"/Project/Details/{project.ProjectID}",
                    ActionText = "Xem dự án",
                    IsRead = false,
                    CreatedAt = DateTime.Now
                });
            }

            await _context.SaveChangesAsync();

            return Json(new
            {
                success = true,
                message = "Gửi yêu cầu tư vấn thành công! Chuyên viên dự án sẽ liên hệ với bạn sớm nhất."
            });
        }
    }
}
using BDSKhanhHoa.Areas.Admin.ViewModels;
using BDSKhanhHoa.Data;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;
using System.Text;

namespace BDSKhanhHoa.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin,Staff")]
    public class DashboardController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly string[] _successStatuses = { "Success", "Completed", "Paid" };
        private readonly string[] _failedStatuses = { "Failed", "Canceled", "Cancelled" };

        public DashboardController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index(
            string? reportType,
            int? year,
            int? quarter,
            int? month,
            DateTime? fromDate,
            DateTime? toDate)
        {
            DateTime now = DateTime.Now;
            int selectedYear = year ?? now.Year;

            ReportRange range = BuildReportRange(reportType, selectedYear, quarter, month, fromDate, toDate);

            DateTime today = now.Date;
            DateTime tomorrow = today.AddDays(1);
            DateTime startOfMonth = new DateTime(now.Year, now.Month, 1);
            DateTime startOfYear = new DateTime(selectedYear, 1, 1);
            DateTime endOfYear = startOfYear.AddYears(1);

            var model = new AdminDashboardViewModel
            {
                SelectedYear = selectedYear
            };

            model.AvailableYears = await GetAvailableYearsAsync(now.Year);

            model.TotalRevenue = await _context.Transactions
                .Where(t => _successStatuses.Contains(t.Status))
                .SumAsync(t => t.Amount);

            model.YearRevenue = await _context.Transactions
                .Where(t => _successStatuses.Contains(t.Status)
                         && t.CreatedAt >= range.StartDate
                         && t.CreatedAt < range.EndDate)
                .SumAsync(t => t.Amount);

            model.MonthlyRevenue = await _context.Transactions
                .Where(t => _successStatuses.Contains(t.Status)
                         && t.CreatedAt >= startOfMonth)
                .SumAsync(t => t.Amount);

            model.TodayRevenue = await _context.Transactions
                .Where(t => _successStatuses.Contains(t.Status)
                         && t.CreatedAt >= today
                         && t.CreatedAt < tomorrow)
                .SumAsync(t => t.Amount);

            model.TotalProperties = await _context.Properties
                .CountAsync(p => p.IsDeleted == false);

            model.PendingProperties = await _context.Properties
                .CountAsync(p => p.Status == "Pending" && p.IsDeleted == false);

            model.ApprovedProperties = await _context.Properties
                .CountAsync(p => p.Status == "Approved" && p.IsDeleted == false);

            model.SoldProperties = await _context.Properties
                .CountAsync(p => p.Status == "Sold" && p.IsDeleted == false);

            model.RentedProperties = await _context.Properties
                .CountAsync(p => p.Status == "Rented" && p.IsDeleted == false);

            model.TotalProjects = await _context.Projects
                .CountAsync(p => p.IsDeleted == false);

            model.PendingProjects = await _context.Projects
                .CountAsync(p => p.ApprovalStatus == "Pending" && p.IsDeleted == false);

            model.TotalUsers = await _context.Users
                .CountAsync(u => u.IsDeleted == false);

            model.NewUsersThisMonth = await _context.Users
                .CountAsync(u => u.CreatedAt >= startOfMonth && u.IsDeleted == false);

            model.PendingReports = await _context.PropertyReports
                .CountAsync(r => r.Status == "Pending" && r.IsDeleted == false);

            model.TotalChatInteractions = await _context.ChatLogs.CountAsync();

            model.ChatInteractionsToday = await _context.ChatLogs
                .CountAsync(c => c.CreatedAt >= today && c.CreatedAt < tomorrow);

            model.TotalTransactions = await _context.Transactions.CountAsync();

            model.SuccessfulTransactions = await _context.Transactions
                .CountAsync(t => _successStatuses.Contains(t.Status));

            model.PendingTransactions = await _context.Transactions
                .CountAsync(t => t.Status == "Pending");

            model.FailedTransactions = await _context.Transactions
                .CountAsync(t => _failedStatuses.Contains(t.Status));

            model.TotalAppointments = await _context.Appointments.CountAsync();

            model.PendingAppointments = await _context.Appointments
                .CountAsync(a => a.Status == "Pending");

            model.TotalConsultations = await _context.Consultations.CountAsync();

            model.PendingConsultations = await _context.Consultations
                .CountAsync(c => c.Status == "Pending");

            List<MonthlyDashboardReportItem> monthlyItems = await BuildMonthlyReportItemsAsync(selectedYear);

            foreach (MonthlyDashboardReportItem item in monthlyItems)
            {
                model.MonthLabels.Add(item.Label);
                model.RevenueData.Add(item.Revenue);
                model.TransactionData.Add(item.Transactions);
                model.PropertyCreatedData.Add(item.NewProperties);
                model.PropertySoldData.Add(item.SoldProperties);
                model.PropertyRentedData.Add(item.RentedProperties);
                model.UserData.Add(item.NewUsers);
                model.ChatData.Add(item.ChatInteractions);
                model.AuditLogData.Add(item.AuditLogs);
            }

            model.PropertyStatusItems = await _context.Properties
                .Where(p => p.IsDeleted == false)
                .GroupBy(p => p.Status)
                .Select(g => new StatusStatisticItem
                {
                    Status = g.Key,
                    DisplayName = g.Key == "Pending" ? "Chờ duyệt"
                        : g.Key == "Approved" ? "Đang hiển thị"
                        : g.Key == "Rejected" ? "Bị từ chối"
                        : g.Key == "Sold" ? "Đã bán"
                        : g.Key == "Rented" ? "Đã thuê"
                        : g.Key == "Hidden" ? "Đã ẩn"
                        : g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Count)
                .ToListAsync();

            model.TransactionStatusItems = await _context.Transactions
                .GroupBy(t => t.Status)
                .Select(g => new StatusStatisticItem
                {
                    Status = g.Key,
                    DisplayName = g.Key == "Success" || g.Key == "Completed" || g.Key == "Paid" ? "Thành công"
                        : g.Key == "Pending" ? "Đang xử lý"
                        : g.Key == "Failed" ? "Thất bại"
                        : g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Count)
                .ToListAsync();

            model.PackageRevenueItems = await _context.Transactions
                .Where(t => _successStatuses.Contains(t.Status)
                         && t.CreatedAt >= range.StartDate
                         && t.CreatedAt < range.EndDate)
                .GroupBy(t => t.Description)
                .Select(g => new PackageRevenueItem
                {
                    PackageName = g.Key ?? "Gói dịch vụ",
                    TotalBuy = g.Count(),
                    TotalRevenue = g.Sum(x => x.Amount)
                })
                .OrderByDescending(x => x.TotalRevenue)
                .Take(4)
                .ToListAsync();

            model.RecentTransactions = await _context.Transactions
                .AsNoTracking()
                .Include(t => t.User)
                .OrderByDescending(t => t.CreatedAt)
                .Take(10)
                .Select(t => new RecentTransactionItem
                {
                    TransactionID = t.TransactionID,
                    TransactionCode = t.TransactionCode,
                    UserName = t.User != null ? (t.User.FullName ?? t.User.Username) : "Không rõ",
                    UserAvatar = t.User != null ? t.User.Avatar : null,
                    Description = t.Description,
                    Amount = t.Amount,
                    Status = t.Status,
                    CreatedAt = t.CreatedAt
                })
                .ToListAsync();

            ViewBag.ReportType = string.IsNullOrWhiteSpace(reportType) ? "year" : reportType;
            ViewBag.SelectedQuarter = quarter;
            ViewBag.SelectedMonth = month;
            ViewBag.FromDate = fromDate?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = toDate?.ToString("yyyy-MM-dd");
            ViewBag.ReportRangeText = range.DisplayName;

            ViewData["Title"] = "Báo cáo thống kê quản trị";

            return View(model);
        }

        public async Task<IActionResult> ExportCsv(
            string? reportType,
            int? year,
            int? quarter,
            int? month,
            DateTime? fromDate,
            DateTime? toDate)
        {
            int selectedYear = year ?? DateTime.Now.Year;
            ReportRange range = BuildReportRange(reportType, selectedYear, quarter, month, fromDate, toDate);
            DashboardExportData data = await BuildExportDataAsync(selectedYear, range);

            var csv = new StringBuilder();
            csv.AppendLine("Bao cao thong ke quan tri");
            csv.AppendLine($"Pham vi,{EscapeCsv(range.DisplayName)}");
            csv.AppendLine($"Ngay xuat,{DateTime.Now:dd/MM/yyyy HH:mm}");
            csv.AppendLine();
            csv.AppendLine("Thang,Doanh thu,Tin moi,Tin da ban,Tin da thue,Nguoi dung moi,Giao dich,Chatbot,Nhat ky hoat dong");

            foreach (MonthlyDashboardReportItem item in data.MonthlyItems)
            {
                csv.AppendLine($"{EscapeCsv(item.Label)},{item.Revenue},{item.NewProperties},{item.SoldProperties},{item.RentedProperties},{item.NewUsers},{item.Transactions},{item.ChatInteractions},{item.AuditLogs}");
            }

            csv.AppendLine();
            csv.AppendLine("Top goi tin / dich vu");
            csv.AppendLine("Goi dich vu,Luot mua,Doanh thu");

            foreach (PackageRevenueItem item in data.PackageItems)
            {
                csv.AppendLine($"{EscapeCsv(item.PackageName)},{item.TotalBuy},{item.TotalRevenue}");
            }

            byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
            string fileName = $"bao-cao-thong-ke-admin-{selectedYear}.csv";

            return File(bytes, "text/csv; charset=utf-8", fileName);
        }

        public async Task<IActionResult> ExportExcel(
            string? reportType,
            int? year,
            int? quarter,
            int? month,
            DateTime? fromDate,
            DateTime? toDate)
        {
            int selectedYear = year ?? DateTime.Now.Year;
            ReportRange range = BuildReportRange(reportType, selectedYear, quarter, month, fromDate, toDate);
            DashboardExportData data = await BuildExportDataAsync(selectedYear, range);

            using var workbook = new XLWorkbook();

            IXLWorksheet overviewSheet = workbook.Worksheets.Add("Tong quan");
            overviewSheet.Cell("A1").Value = "BÁO CÁO THỐNG KÊ QUẢN TRỊ";
            overviewSheet.Range("A1:F1").Merge();
            overviewSheet.Cell("A1").Style.Font.Bold = true;
            overviewSheet.Cell("A1").Style.Font.FontSize = 18;
            overviewSheet.Cell("A1").Style.Font.FontColor = XLColor.White;
            overviewSheet.Cell("A1").Style.Fill.BackgroundColor = XLColor.FromHtml("#1D4ED8");
            overviewSheet.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            overviewSheet.Cell("A3").Value = "Phạm vi";
            overviewSheet.Cell("B3").Value = range.DisplayName;
            overviewSheet.Cell("A4").Value = "Ngày xuất";
            overviewSheet.Cell("B4").Value = DateTime.Now.ToString("dd/MM/yyyy HH:mm");

            overviewSheet.Cell("A6").Value = "Chỉ số";
            overviewSheet.Cell("B6").Value = "Giá trị";
            overviewSheet.Range("A6:B6").Style.Font.Bold = true;
            overviewSheet.Range("A6:B6").Style.Fill.BackgroundColor = XLColor.FromHtml("#DBEAFE");

            overviewSheet.Cell("A7").Value = "Tổng doanh thu";
            overviewSheet.Cell("B7").Value = data.TotalRevenue;

            overviewSheet.Cell("A8").Value = "Doanh thu kỳ báo cáo";
            overviewSheet.Cell("B8").Value = data.RangeRevenue;

            overviewSheet.Cell("A9").Value = "Tổng tin đăng";
            overviewSheet.Cell("B9").Value = data.TotalProperties;

            overviewSheet.Cell("A10").Value = "Tin đã bán";
            overviewSheet.Cell("B10").Value = data.SoldProperties;

            overviewSheet.Cell("A11").Value = "Tin đã thuê";
            overviewSheet.Cell("B11").Value = data.RentedProperties;

            overviewSheet.Cell("A12").Value = "Người dùng";
            overviewSheet.Cell("B12").Value = data.TotalUsers;

            overviewSheet.Cell("A13").Value = "Giao dịch thành công";
            overviewSheet.Cell("B13").Value = data.SuccessfulTransactions;

            overviewSheet.Cell("A14").Value = "Chatbot AI";
            overviewSheet.Cell("B14").Value = data.TotalChatInteractions;

            overviewSheet.Range("B7:B14").Style.NumberFormat.Format = "#,##0";
            overviewSheet.Columns().AdjustToContents();

            IXLWorksheet monthlySheet = workbook.Worksheets.Add("Thong ke thang");
            monthlySheet.Cell("A1").Value = "Tháng";
            monthlySheet.Cell("B1").Value = "Doanh thu";
            monthlySheet.Cell("C1").Value = "Tin mới";
            monthlySheet.Cell("D1").Value = "Tin đã bán";
            monthlySheet.Cell("E1").Value = "Tin đã thuê";
            monthlySheet.Cell("F1").Value = "Người dùng mới";
            monthlySheet.Cell("G1").Value = "Giao dịch";
            monthlySheet.Cell("H1").Value = "Chatbot";
            monthlySheet.Cell("I1").Value = "Nhật ký";

            monthlySheet.Range("A1:I1").Style.Font.Bold = true;
            monthlySheet.Range("A1:I1").Style.Fill.BackgroundColor = XLColor.FromHtml("#1D4ED8");
            monthlySheet.Range("A1:I1").Style.Font.FontColor = XLColor.White;

            int row = 2;
            foreach (MonthlyDashboardReportItem item in data.MonthlyItems)
            {
                monthlySheet.Cell(row, 1).Value = item.Label;
                monthlySheet.Cell(row, 2).Value = item.Revenue;
                monthlySheet.Cell(row, 3).Value = item.NewProperties;
                monthlySheet.Cell(row, 4).Value = item.SoldProperties;
                monthlySheet.Cell(row, 5).Value = item.RentedProperties;
                monthlySheet.Cell(row, 6).Value = item.NewUsers;
                monthlySheet.Cell(row, 7).Value = item.Transactions;
                monthlySheet.Cell(row, 8).Value = item.ChatInteractions;
                monthlySheet.Cell(row, 9).Value = item.AuditLogs;
                row++;
            }

            monthlySheet.Range($"A1:I{row - 1}").Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            monthlySheet.Range($"A1:I{row - 1}").Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            monthlySheet.Column(2).Style.NumberFormat.Format = "#,##0";
            monthlySheet.Columns().AdjustToContents();

            IXLWorksheet packageSheet = workbook.Worksheets.Add("Goi dich vu");
            packageSheet.Cell("A1").Value = "Gói / dịch vụ";
            packageSheet.Cell("B1").Value = "Lượt mua";
            packageSheet.Cell("C1").Value = "Doanh thu";
            packageSheet.Range("A1:C1").Style.Font.Bold = true;
            packageSheet.Range("A1:C1").Style.Fill.BackgroundColor = XLColor.FromHtml("#16A34A");
            packageSheet.Range("A1:C1").Style.Font.FontColor = XLColor.White;

            row = 2;
            foreach (PackageRevenueItem item in data.PackageItems)
            {
                packageSheet.Cell(row, 1).Value = item.PackageName;
                packageSheet.Cell(row, 2).Value = item.TotalBuy;
                packageSheet.Cell(row, 3).Value = item.TotalRevenue;
                row++;
            }

            packageSheet.Column(3).Style.NumberFormat.Format = "#,##0";
            packageSheet.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            byte[] bytes = stream.ToArray();

            string fileName = $"bao-cao-thong-ke-admin-{selectedYear}.xlsx";
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

        public async Task<IActionResult> ExportWord(
            string? reportType,
            int? year,
            int? quarter,
            int? month,
            DateTime? fromDate,
            DateTime? toDate)
        {
            int selectedYear = year ?? DateTime.Now.Year;
            ReportRange range = BuildReportRange(reportType, selectedYear, quarter, month, fromDate, toDate);
            DashboardExportData data = await BuildExportDataAsync(selectedYear, range);

            using var stream = new MemoryStream();

            using (WordprocessingDocument document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
            {
                MainDocumentPart mainPart = document.AddMainDocumentPart();
                mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
                Body body = mainPart.Document.AppendChild(new Body());

                SectionProperties sectionProperties = new SectionProperties(
                    new PageMargin
                    {
                        Top = 720,
                        Right = 720,
                        Bottom = 720,
                        Left = 720
                    });

                body.Append(CreateParagraph("BÁO CÁO THỐNG KÊ QUẢN TRỊ", 30, true, JustificationValues.Center));
                body.Append(CreateParagraph("Website kinh doanh bất động sản Khánh Hòa", 22, false, JustificationValues.Center));
                body.Append(CreateParagraph($"Phạm vi: {range.DisplayName}", 21, false, JustificationValues.Center));
                body.Append(CreateParagraph($"Ngày xuất: {DateTime.Now:dd/MM/yyyy HH:mm}", 20, false, JustificationValues.Center));
                body.Append(CreateParagraph("", 10, false, JustificationValues.Left));

                body.Append(CreateParagraph("1. Tổng quan chỉ số", 24, true, JustificationValues.Left));

                Table overviewTable = CreateWordTable(new[]
                {
                    new[] { "Chỉ số", "Giá trị" },
                    new[] { "Tổng doanh thu", FormatCurrency(data.TotalRevenue) },
                    new[] { "Doanh thu kỳ báo cáo", FormatCurrency(data.RangeRevenue) },
                    new[] { "Tổng tin đăng", data.TotalProperties.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) },
                    new[] { "Tin đã bán", data.SoldProperties.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) },
                    new[] { "Tin đã thuê", data.RentedProperties.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) },
                    new[] { "Người dùng", data.TotalUsers.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) },
                    new[] { "Giao dịch thành công", data.SuccessfulTransactions.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) },
                    new[] { "Tổng lượt hỏi Chatbot AI", data.TotalChatInteractions.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) }
                });

                body.Append(overviewTable);
                body.Append(CreateParagraph("", 10, false, JustificationValues.Left));
                body.Append(CreateParagraph("2. Thống kê theo tháng", 24, true, JustificationValues.Left));

                List<string[]> monthlyRows = new List<string[]>
                {
                    new[] { "Tháng", "Doanh thu", "Tin mới", "Đã bán", "Đã thuê", "User mới", "GD", "AI" }
                };

                foreach (MonthlyDashboardReportItem item in data.MonthlyItems)
                {
                    monthlyRows.Add(new[]
                    {
                        item.Label,
                        FormatCurrency(item.Revenue),
                        item.NewProperties.ToString(),
                        item.SoldProperties.ToString(),
                        item.RentedProperties.ToString(),
                        item.NewUsers.ToString(),
                        item.Transactions.ToString(),
                        item.ChatInteractions.ToString()
                    });
                }

                body.Append(CreateWordTable(monthlyRows.ToArray()));
                body.Append(CreateParagraph("", 10, false, JustificationValues.Left));
                body.Append(CreateParagraph("3. Top gói tin / dịch vụ", 24, true, JustificationValues.Left));

                List<string[]> packageRows = new List<string[]>
                {
                    new[] { "Gói dịch vụ", "Lượt mua", "Doanh thu" }
                };

                foreach (PackageRevenueItem item in data.PackageItems)
                {
                    packageRows.Add(new[]
                    {
                        item.PackageName,
                        item.TotalBuy.ToString(),
                        FormatCurrency(item.TotalRevenue)
                    });
                }

                body.Append(CreateWordTable(packageRows.ToArray()));
                body.Append(sectionProperties);

                mainPart.Document.Save();
            }

            byte[] bytes = stream.ToArray();
            string fileName = $"bao-cao-thong-ke-admin-{selectedYear}.docx";

            return File(bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
        }

        public async Task<IActionResult> ExportPdf(
            string? reportType,
            int? year,
            int? quarter,
            int? month,
            DateTime? fromDate,
            DateTime? toDate)
        {
            QuestPDF.Settings.License = LicenseType.Community;

            int selectedYear = year ?? DateTime.Now.Year;
            ReportRange range = BuildReportRange(reportType, selectedYear, quarter, month, fromDate, toDate);
            DashboardExportData data = await BuildExportDataAsync(selectedYear, range);

            byte[] pdfBytes = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(24);
                    page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Arial"));

                    page.Header().Column(column =>
                    {
                        column.Item().Text("BÁO CÁO THỐNG KÊ QUẢN TRỊ")
                            .FontSize(18)
                            .Bold()
                            .FontColor(Colors.Blue.Darken3);

                        column.Item().Text($"Website kinh doanh bất động sản Khánh Hòa · {range.DisplayName}")
                            .FontSize(10)
                            .FontColor(Colors.Grey.Darken2);

                        column.Item().Text($"Ngày xuất: {DateTime.Now:dd/MM/yyyy HH:mm}")
                            .FontSize(9)
                            .FontColor(Colors.Grey.Darken1);

                        column.Item().PaddingTop(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                    });

                    page.Content().PaddingTop(12).Column(column =>
                    {
                        column.Spacing(12);

                        column.Item().Row(row =>
                        {
                            row.RelativeItem().Element(x => PdfMetricBox(x, "Tổng doanh thu", FormatCurrency(data.TotalRevenue), Colors.Blue.Lighten5));
                            row.RelativeItem().Element(x => PdfMetricBox(x, "Doanh thu kỳ báo cáo", FormatCurrency(data.RangeRevenue), Colors.Green.Lighten5));
                            row.RelativeItem().Element(x => PdfMetricBox(x, "Tin đã bán", data.SoldProperties.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")), Colors.Orange.Lighten5));
                            row.RelativeItem().Element(x => PdfMetricBox(x, "Tin đã thuê", data.RentedProperties.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")), Colors.Purple.Lighten5));
                        });

                        column.Item().Text("Thống kê theo tháng")
                            .FontSize(13)
                            .Bold()
                            .FontColor(Colors.Blue.Darken3);

                        column.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn(1.1f);
                                columns.RelativeColumn(1.6f);
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                                columns.RelativeColumn();
                            });

                            PdfHeaderCell(table, "Tháng");
                            PdfHeaderCell(table, "Doanh thu");
                            PdfHeaderCell(table, "Tin mới");
                            PdfHeaderCell(table, "Đã bán");
                            PdfHeaderCell(table, "Đã thuê");
                            PdfHeaderCell(table, "User mới");
                            PdfHeaderCell(table, "GD");
                            PdfHeaderCell(table, "AI");
                            PdfHeaderCell(table, "Log");

                            foreach (MonthlyDashboardReportItem item in data.MonthlyItems)
                            {
                                PdfBodyCell(table, item.Label);
                                PdfBodyCell(table, FormatCurrency(item.Revenue));
                                PdfBodyCell(table, item.NewProperties.ToString());
                                PdfBodyCell(table, item.SoldProperties.ToString());
                                PdfBodyCell(table, item.RentedProperties.ToString());
                                PdfBodyCell(table, item.NewUsers.ToString());
                                PdfBodyCell(table, item.Transactions.ToString());
                                PdfBodyCell(table, item.ChatInteractions.ToString());
                                PdfBodyCell(table, item.AuditLogs.ToString());
                            }
                        });

                        column.Item().Text("Top gói tin / dịch vụ")
                            .FontSize(13)
                            .Bold()
                            .FontColor(Colors.Blue.Darken3);

                        column.Item().Table(table =>
                        {
                            table.ColumnsDefinition(columns =>
                            {
                                columns.RelativeColumn(3);
                                columns.RelativeColumn(1);
                                columns.RelativeColumn(1.5f);
                            });

                            PdfHeaderCell(table, "Gói dịch vụ");
                            PdfHeaderCell(table, "Lượt mua");
                            PdfHeaderCell(table, "Doanh thu");

                            foreach (PackageRevenueItem item in data.PackageItems)
                            {
                                PdfBodyCell(table, item.PackageName);
                                PdfBodyCell(table, item.TotalBuy.ToString());
                                PdfBodyCell(table, FormatCurrency(item.TotalRevenue));
                            }
                        });
                    });

                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.Span("Trang ");
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                });
            }).GeneratePdf();

            string fileName = $"bao-cao-thong-ke-admin-{selectedYear}.pdf";
            return File(pdfBytes, "application/pdf", fileName);
        }

        private async Task<DashboardExportData> BuildExportDataAsync(int selectedYear, ReportRange range)
        {
            var data = new DashboardExportData();

            data.MonthlyItems = await BuildMonthlyReportItemsAsync(selectedYear);

            data.TotalRevenue = await _context.Transactions
                .Where(t => _successStatuses.Contains(t.Status))
                .SumAsync(t => t.Amount);

            data.RangeRevenue = await _context.Transactions
                .Where(t => _successStatuses.Contains(t.Status)
                         && t.CreatedAt >= range.StartDate
                         && t.CreatedAt < range.EndDate)
                .SumAsync(t => t.Amount);

            data.TotalProperties = await _context.Properties
                .CountAsync(p => p.IsDeleted == false);

            data.SoldProperties = await _context.Properties
                .CountAsync(p => p.IsDeleted == false && p.Status == "Sold");

            data.RentedProperties = await _context.Properties
                .CountAsync(p => p.IsDeleted == false && p.Status == "Rented");

            data.TotalUsers = await _context.Users
                .CountAsync(u => u.IsDeleted == false);

            data.SuccessfulTransactions = await _context.Transactions
                .CountAsync(t => _successStatuses.Contains(t.Status));

            data.TotalChatInteractions = await _context.ChatLogs.CountAsync();

            data.PackageItems = await _context.Transactions
                .Where(t => _successStatuses.Contains(t.Status)
                         && t.CreatedAt >= range.StartDate
                         && t.CreatedAt < range.EndDate)
                .GroupBy(t => t.Description)
                .Select(g => new PackageRevenueItem
                {
                    PackageName = g.Key ?? "Gói dịch vụ",
                    TotalBuy = g.Count(),
                    TotalRevenue = g.Sum(x => x.Amount)
                })
                .OrderByDescending(x => x.TotalRevenue)
                .Take(4)
                .ToListAsync();

            return data;
        }

        private async Task<List<MonthlyDashboardReportItem>> BuildMonthlyReportItemsAsync(int selectedYear)
        {
            var items = new List<MonthlyDashboardReportItem>();

            for (int month = 1; month <= 12; month++)
            {
                DateTime monthStart = new DateTime(selectedYear, month, 1);
                DateTime monthEnd = monthStart.AddMonths(1);

                decimal revenue = await _context.Transactions
                    .Where(t => _successStatuses.Contains(t.Status)
                             && t.CreatedAt >= monthStart
                             && t.CreatedAt < monthEnd)
                    .SumAsync(t => t.Amount);

                int transactions = await _context.Transactions
                    .CountAsync(t => t.CreatedAt >= monthStart && t.CreatedAt < monthEnd);

                int newProperties = await _context.Properties
                    .CountAsync(p => p.IsDeleted == false
                                  && p.CreatedAt >= monthStart
                                  && p.CreatedAt < monthEnd);

                int sold = await _context.Properties
                    .CountAsync(p => p.IsDeleted == false
                                  && p.Status == "Sold"
                                  && p.UpdatedAt >= monthStart
                                  && p.UpdatedAt < monthEnd);

                int rented = await _context.Properties
                    .CountAsync(p => p.IsDeleted == false
                                  && p.Status == "Rented"
                                  && p.UpdatedAt >= monthStart
                                  && p.UpdatedAt < monthEnd);

                int users = await _context.Users
                    .CountAsync(u => u.IsDeleted == false
                                  && u.CreatedAt >= monthStart
                                  && u.CreatedAt < monthEnd);

                int chats = await _context.ChatLogs
                    .CountAsync(c => c.CreatedAt >= monthStart && c.CreatedAt < monthEnd);

                int logs = await _context.AuditLogs
                    .CountAsync(a => a.CreatedAt >= monthStart && a.CreatedAt < monthEnd);

                items.Add(new MonthlyDashboardReportItem
                {
                    Label = $"T{month}/{selectedYear}",
                    Revenue = revenue,
                    NewProperties = newProperties,
                    SoldProperties = sold,
                    RentedProperties = rented,
                    NewUsers = users,
                    Transactions = transactions,
                    ChatInteractions = chats,
                    AuditLogs = logs
                });
            }

            return items;
        }

        private ReportRange BuildReportRange(string? reportType, int selectedYear, int? quarter, int? month, DateTime? fromDate, DateTime? toDate)
        {
            string type = string.IsNullOrWhiteSpace(reportType) ? "year" : reportType.Trim().ToLower();

            if (type == "custom" && fromDate.HasValue && toDate.HasValue)
            {
                DateTime start = fromDate.Value.Date;
                DateTime end = toDate.Value.Date.AddDays(1);

                if (end <= start)
                {
                    end = start.AddDays(1);
                }

                return new ReportRange
                {
                    StartDate = start,
                    EndDate = end,
                    DisplayName = $"Từ {start:dd/MM/yyyy} đến {end.AddDays(-1):dd/MM/yyyy}"
                };
            }

            if (type == "month" && month.HasValue && month.Value >= 1 && month.Value <= 12)
            {
                DateTime start = new DateTime(selectedYear, month.Value, 1);
                return new ReportRange
                {
                    StartDate = start,
                    EndDate = start.AddMonths(1),
                    DisplayName = $"Tháng {month.Value}/{selectedYear}"
                };
            }

            if (type == "quarter" && quarter.HasValue && quarter.Value >= 1 && quarter.Value <= 4)
            {
                int startMonth = ((quarter.Value - 1) * 3) + 1;
                DateTime start = new DateTime(selectedYear, startMonth, 1);

                return new ReportRange
                {
                    StartDate = start,
                    EndDate = start.AddMonths(3),
                    DisplayName = $"Quý {quarter.Value}/{selectedYear}"
                };
            }

            DateTime startYear = new DateTime(selectedYear, 1, 1);

            return new ReportRange
            {
                StartDate = startYear,
                EndDate = startYear.AddYears(1),
                DisplayName = $"Năm {selectedYear}"
            };
        }

        private async Task<List<int>> GetAvailableYearsAsync(int currentYear)
        {
            var years = await _context.Transactions
                .Select(t => t.CreatedAt.Year)
                .Distinct()
                .OrderByDescending(y => y)
                .ToListAsync();

            if (!years.Contains(currentYear))
            {
                years.Insert(0, currentYear);
            }

            return years;
        }

        private string FormatCurrency(decimal amount)
        {
            return amount.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " đ";
        }

        private string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            string escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        private Paragraph CreateParagraph(string text, int fontSize, bool bold, JustificationValues justification)
        {
            RunProperties runProperties = new RunProperties
            {
                FontSize = new FontSize { Val = fontSize.ToString() }
            };

            runProperties.Append(new RunFonts { Ascii = "Times New Roman", HighAnsi = "Times New Roman" });

            if (bold)
            {
                runProperties.Append(new Bold());
            }

            Run run = new Run();
            run.Append(runProperties);
            run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

            Paragraph paragraph = new Paragraph();
            paragraph.Append(new ParagraphProperties(new Justification { Val = justification }));
            paragraph.Append(run);

            return paragraph;
        }

        private Table CreateWordTable(string[][] rows)
        {
            Table table = new Table();

            TableProperties properties = new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 6 },
                    new BottomBorder { Val = BorderValues.Single, Size = 6 },
                    new LeftBorder { Val = BorderValues.Single, Size = 6 },
                    new RightBorder { Val = BorderValues.Single, Size = 6 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 6 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 6 }
                ),
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }
            );

            table.AppendChild(properties);

            for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                TableRow row = new TableRow();

                foreach (string cellText in rows[rowIndex])
                {
                    TableCell cell = new TableCell();

                    TableCellProperties cellProperties = new TableCellProperties(
                        new TableCellMargin(
                            new TopMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                            new BottomMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                            new LeftMargin { Width = "90", Type = TableWidthUnitValues.Dxa },
                            new RightMargin { Width = "90", Type = TableWidthUnitValues.Dxa }
                        )
                    );

                    if (rowIndex == 0)
                    {
                        cellProperties.Append(new Shading { Fill = "1D4ED8" });
                    }

                    cell.Append(cellProperties);
                    cell.Append(CreateParagraph(cellText, 18, rowIndex == 0, JustificationValues.Left));
                    row.Append(cell);
                }

                table.Append(row);
            }

            return table;
        }

        private void PdfMetricBox(IContainer container, string label, string value, string backgroundColor)
        {
            container
                .PaddingRight(8)
                .Background(backgroundColor)
                .Border(1)
                .BorderColor(Colors.Grey.Lighten2)
                .Padding(10)
                .Column(column =>
                {
                    column.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Darken2);
                    column.Item().Text(value).FontSize(13).Bold().FontColor(Colors.Blue.Darken3);
                });
        }

        private void PdfHeaderCell(TableDescriptor table, string text)
        {
            table.Cell()
                .Background(Colors.Blue.Darken3)
                .Border(1)
                .BorderColor(Colors.White)
                .Padding(5)
                .Text(text)
                .FontColor(Colors.White)
                .Bold()
                .FontSize(8);
        }

        private void PdfBodyCell(TableDescriptor table, string text)
        {
            table.Cell()
                .BorderBottom(1)
                .BorderColor(Colors.Grey.Lighten2)
                .Padding(5)
                .Text(text)
                .FontSize(8);
        }

        private class ReportRange
        {
            public DateTime StartDate { get; set; }
            public DateTime EndDate { get; set; }
            public string DisplayName { get; set; } = "";
        }

        private class MonthlyDashboardReportItem
        {
            public string Label { get; set; } = "";
            public decimal Revenue { get; set; }
            public int NewProperties { get; set; }
            public int SoldProperties { get; set; }
            public int RentedProperties { get; set; }
            public int NewUsers { get; set; }
            public int Transactions { get; set; }
            public int ChatInteractions { get; set; }
            public int AuditLogs { get; set; }
        }

        private class DashboardExportData
        {
            public decimal TotalRevenue { get; set; }
            public decimal RangeRevenue { get; set; }
            public int TotalProperties { get; set; }
            public int SoldProperties { get; set; }
            public int RentedProperties { get; set; }
            public int TotalUsers { get; set; }
            public int SuccessfulTransactions { get; set; }
            public int TotalChatInteractions { get; set; }
            public List<MonthlyDashboardReportItem> MonthlyItems { get; set; } = new();
            public List<PackageRevenueItem> PackageItems { get; set; } = new();
        }
    }
}
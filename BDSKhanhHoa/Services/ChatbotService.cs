using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Services
{
    public class ChatbotService
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _config;
        private readonly ILogger<ChatbotService> _logger;
        private readonly HttpClient _httpClient;

        public ChatbotService(
            ApplicationDbContext context,
            IConfiguration config,
            ILogger<ChatbotService> logger)
        {
            _context = context;
            _config = config;
            _logger = logger;

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(45)
            };
        }

        public async Task<ChatResponse> ProcessChatAsync(ChatRequest req)
        {
            string originalMessage = (req.Message ?? "").Trim();

            if (string.IsNullOrWhiteSpace(originalMessage))
            {
                return new ChatResponse
                {
                    Message = "Bạn vui lòng nhập nội dung cần hỏi nhé.",
                    Intent = "Empty",
                    ShouldShowSuggestions = false,
                    SuggestedProperties = new List<object>()
                };
            }

            string normalizedMessage = NormalizeText(originalMessage);

            ChatIntent intent = DetectIntent(normalizedMessage, req.PageContext);

            List<Property> suggestedProperties = new();

            if (intent.ShouldSearchProperties)
            {
                suggestedProperties = await SearchPropertiesAsync(normalizedMessage, intent);
            }

            string ragData = await GetRagDataAsync();
            string systemKnowledge = BuildSystemKnowledge();
            string currentPageContext = BuildCurrentPageContext(req.PageContext);
            string propertySuggestionContext = BuildSuggestedPropertiesContext(suggestedProperties, intent.ShouldSearchProperties);

            string prompt = BuildPrompt(
                userMessage: originalMessage,
                normalizedUserMessage: normalizedMessage,
                systemKnowledge: systemKnowledge,
                ragData: ragData,
                currentPageContext: currentPageContext,
                propertySuggestionContext: propertySuggestionContext,
                intent: intent);

            string botMessage = await CallGeminiWithRetryAndFallbackAsync(prompt);

            if (string.IsNullOrWhiteSpace(botMessage))
            {
                botMessage = BuildSmartFallbackMessage(intent, suggestedProperties, req.PageContext);
            }

            botMessage = CleanBotMessage(botMessage);

            await SaveChatLogAsync(req, botMessage);

            bool shouldShowSuggestions = intent.ShouldSearchProperties && suggestedProperties.Any();

            return new ChatResponse
            {
                Message = botMessage,
                Intent = intent.Name,
                ShouldShowSuggestions = shouldShowSuggestions,
                SuggestedProperties = shouldShowSuggestions
                    ? suggestedProperties.Select(p => (object)new
                    {
                        id = p.PropertyID,
                        title = p.Title,
                        price = FormatPrice(p.Price),
                        areaSize = p.AreaSize.HasValue ? $"{p.AreaSize.Value:0.##} m²" : "",
                        location = BuildLocationText(p),
                        image = string.IsNullOrWhiteSpace(p.MainImage) ? "/images/no-image.png" : p.MainImage,
                        link = $"/Property/Details/{p.PropertyID}"
                    }).ToList()
                    : new List<object>()
            };
        }

        // =====================================================
        // 1. NHẬN DIỆN Ý ĐỊNH NGƯỜI DÙNG
        // =====================================================
        private static ChatIntent DetectIntent(string message, string? pageContext)
        {
            bool isViewingProperty = !string.IsNullOrWhiteSpace(pageContext);

            bool asksCurrentProperty =
                isViewingProperty &&
                ContainsAny(message,
                    "tin nay",
                    "bai nay",
                    "can nay",
                    "nha nay",
                    "dat nay",
                    "lo nay",
                    "bat dong san nay",
                    "bds nay",
                    "cho nay",
                    "o day",
                    "can tren",
                    "tin dang nay",
                    "gia nay",
                    "vi tri nay",
                    "phap ly nay",
                    "co nen mua",
                    "co nen thue",
                    "nen mua khong",
                    "nen thue khong",
                    "xem thuc te",
                    "dat lich",
                    "hen xem",
                    "lien he chu nha",
                    "lien he nguoi dang",
                    "tin nay co tot khong",
                    "tin nay on khong",
                    "tin nay hop ly khong",
                    "can nay hop ly khong",
                    "lo nay hop ly khong");

            bool asksSearchExplicitly =
                ContainsAny(message,
                    "tim",
                    "tim kiem",
                    "kiem giup",
                    "loc giup",
                    "goi y",
                    "de xuat",
                    "cho toi xem",
                    "cho minh xem",
                    "co can nao",
                    "co nha nao",
                    "co dat nao",
                    "co bds nao",
                    "bat dong san nao",
                    "danh sach",
                    "can mua",
                    "can thue",
                    "muon mua",
                    "muon thue",
                    "toi muon mua",
                    "toi muon thue",
                    "minh muon mua",
                    "minh muon thue",
                    "anh muon mua",
                    "chi muon thue",
                    "tim can khac",
                    "goi y can khac",
                    "co tin nao khac",
                    "bat dong san khac",
                    "nha gia tot",
                    "dat gia tot",
                    "can ho gia tot",
                    "co gi phu hop",
                    "co lua chon nao",
                    "co san pham nao",
                    "co tin nao");

            bool hasTransactionIntent =
                ContainsAny(message,
                    "mua",
                    "ban",
                    "thue",
                    "muon",
                    "can",
                    "dau tu",
                    "o thuc",
                    "kinh doanh",
                    "tim cho",
                    "tim mua",
                    "tim thue",
                    "cho thue",
                    "sang nhuong");

            bool hasPropertyType =
                ContainsAny(message,
                    "nha",
                    "dat",
                    "can ho",
                    "chung cu",
                    "phong tro",
                    "mat bang",
                    "biet thu",
                    "villa",
                    "shophouse",
                    "kho",
                    "xuong",
                    "van phong",
                    "toa nha",
                    "nha pho",
                    "nha rieng",
                    "nha nguyen can",
                    "du an",
                    "bds",
                    "bat dong san",
                    "lo dat",
                    "dat nen");

            bool hasAreaOrBudget =
                ContainsAny(message,
                    "nha trang",
                    "cam ranh",
                    "ninh hoa",
                    "van ninh",
                    "dien khanh",
                    "cam lam",
                    "khanh vinh",
                    "khanh son",
                    "truong sa",
                    "phan rang",
                    "ninh thuan",
                    "ninh hai",
                    "ninh phuoc",
                    "thuan nam",
                    "thuan bac",
                    "ninh son",
                    "bac ai",
                    "ninh chu",
                    "vinh hy",
                    "ca na",
                    "duoi",
                    "tren",
                    "tam",
                    "khoang",
                    "tu",
                    "den",
                    "ty",
                    "ti",
                    "trieu",
                    "m2",
                    "met vuong",
                    "ngan sach",
                    "gia",
                    "dien tich");

            bool broadSearchByMeaning =
                hasTransactionIntent &&
                hasPropertyType &&
                hasAreaOrBudget &&
                !asksCurrentProperty;

            bool shouldSearchProperties = asksSearchExplicitly || broadSearchByMeaning;

            bool wantsOtherOptions =
                ContainsAny(message,
                    "khac",
                    "can khac",
                    "tin khac",
                    "goi y them",
                    "tim them",
                    "lua chon khac",
                    "xem them",
                    "so sanh them");

            if (asksCurrentProperty && !wantsOtherOptions)
            {
                shouldSearchProperties = false;
            }

            string name = "General";

            if (asksCurrentProperty)
            {
                name = "CurrentPropertyAdvice";
            }
            else if (shouldSearchProperties)
            {
                name = "PropertySearch";
            }
            else if (ContainsAny(message,
                         "gia goi",
                         "goi vip",
                         "bao gia",
                         "phi dang tin",
                         "dang tin",
                         "nap tien",
                         "thanh toan",
                         "kim cuong",
                         "vip",
                         "voucher",
                         "ma giam gia",
                         "goi dong",
                         "goi bac",
                         "goi vang"))
            {
                name = "PackagePolicy";
            }
            else if (ContainsAny(message,
                         "phap ly",
                         "so do",
                         "so hong",
                         "quy hoach",
                         "hop dong",
                         "cong chung",
                         "dat quy hoach",
                         "tranh chap",
                         "giay to",
                         "chuyen nhuong",
                         "dat coc",
                         "the chap",
                         "lo gioi",
                         "hoan cong"))
            {
                name = "LegalAdvice";
            }
            else if (ContainsAny(message,
                         "huong dan",
                         "dang ky",
                         "dang nhap",
                         "quen mat khau",
                         "doi mat khau",
                         "tai khoan",
                         "ho so",
                         "binh luan",
                         "bao cao vi pham",
                         "yeu thich",
                         "dat lich",
                         "yeu cau tu van",
                         "lien he",
                         "chat truc tiep"))
            {
                name = "WebsiteGuide";
            }
            else if (ContainsAny(message,
                         "vay ngan hang",
                         "vay mua nha",
                         "lai suat",
                         "tra gop",
                         "goc lai",
                         "tinh lai",
                         "khoan vay",
                         "vay bao nhieu",
                         "vay von"))
            {
                name = "LoanAdvice";
            }

            return new ChatIntent
            {
                Name = name,
                IsViewingProperty = isViewingProperty,
                IsAskingAboutCurrentProperty = asksCurrentProperty,
                ShouldSearchProperties = shouldSearchProperties,
                WantsBuy = ContainsAny(message, "mua", "ban", "can mua", "muon mua", "dau tu", "tim mua"),
                WantsRent = ContainsAny(message, "thue", "muon thue", "can thue", "cho thue", "tim thue"),
                WantsOtherOptions = wantsOtherOptions
            };
        }

        // =====================================================
        // 2. TÌM BẤT ĐỘNG SẢN TRONG SQL
        // =====================================================
        private async Task<List<Property>> SearchPropertiesAsync(string message, ChatIntent intent)
        {
            IQueryable<Property> query = _context.Properties
                .AsNoTracking()
                .Include(p => p.Ward)
                    .ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p =>
                    p.IsDeleted != true &&
                    p.Status == "Approved");

            if (intent.WantsRent && !intent.WantsBuy)
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("thuê") ||
                        p.PropertyType.TypeName.ToLower().Contains("thue") ||
                        p.PropertyType.TypeName.ToLower().Contains("cho thuê") ||
                        p.PropertyType.TypeName.ToLower().Contains("cho thue") ||
                        p.PropertyType.ParentID == 2
                    ));
            }
            else if (intent.WantsBuy && !intent.WantsRent)
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("bán") ||
                        p.PropertyType.TypeName.ToLower().Contains("ban") ||
                        p.PropertyType.ParentID == 1
                    ));
            }

            query = ApplyPropertyTypeFilter(query, message);
            query = ApplyLocationFilter(query, message);
            query = ApplyAreaSizeFilter(query, message);
            query = ApplyPriceFilter(query, message);

            DateTime now = DateTime.Now;

            List<Property> result = await query
                .OrderByDescending(p => p.VipExpiryDate.HasValue && p.VipExpiryDate.Value > now)
                .ThenBy(p => p.PostServicePackage != null ? p.PostServicePackage.PriorityLevel : 999)
                .ThenByDescending(p => p.CreatedAt)
                .Take(4)
                .ToListAsync();

            return result;
        }

        private static IQueryable<Property> ApplyPropertyTypeFilter(IQueryable<Property> query, string message)
        {
            if (ContainsAny(message, "can ho", "chung cu", "apartment"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("căn hộ") ||
                        p.PropertyType.TypeName.ToLower().Contains("can ho") ||
                        p.PropertyType.TypeName.ToLower().Contains("chung cư") ||
                        p.PropertyType.TypeName.ToLower().Contains("chung cu") ||
                        p.Title.ToLower().Contains("căn hộ") ||
                        p.Title.ToLower().Contains("can ho") ||
                        p.Title.ToLower().Contains("chung cư") ||
                        p.Title.ToLower().Contains("chung cu")
                    ));
            }
            else if (ContainsAny(message, "phong tro", "tro", "mini"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("phòng trọ") ||
                        p.PropertyType.TypeName.ToLower().Contains("phong tro") ||
                        p.PropertyType.TypeName.ToLower().Contains("trọ") ||
                        p.PropertyType.TypeName.ToLower().Contains("tro") ||
                        p.PropertyType.TypeName.ToLower().Contains("mini") ||
                        p.Title.ToLower().Contains("phòng trọ") ||
                        p.Title.ToLower().Contains("phong tro") ||
                        p.Title.ToLower().Contains("trọ") ||
                        p.Title.ToLower().Contains("tro")
                    ));
            }
            else if (ContainsAny(message, "dat", "dat nen", "lo dat"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("đất") ||
                        p.PropertyType.TypeName.ToLower().Contains("dat") ||
                        p.Title.ToLower().Contains("đất") ||
                        p.Title.ToLower().Contains("dat")
                    ));
            }
            else if (ContainsAny(message, "biet thu", "villa"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("biệt thự") ||
                        p.PropertyType.TypeName.ToLower().Contains("biet thu") ||
                        p.PropertyType.TypeName.ToLower().Contains("villa") ||
                        p.Title.ToLower().Contains("biệt thự") ||
                        p.Title.ToLower().Contains("biet thu") ||
                        p.Title.ToLower().Contains("villa")
                    ));
            }
            else if (ContainsAny(message, "mat bang", "kinh doanh", "cua hang", "shop"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("mặt bằng") ||
                        p.PropertyType.TypeName.ToLower().Contains("mat bang") ||
                        p.PropertyType.TypeName.ToLower().Contains("kinh doanh") ||
                        p.Title.ToLower().Contains("mặt bằng") ||
                        p.Title.ToLower().Contains("mat bang") ||
                        p.Title.ToLower().Contains("kinh doanh") ||
                        p.Title.ToLower().Contains("shop") ||
                        p.Title.ToLower().Contains("cửa hàng") ||
                        p.Title.ToLower().Contains("cua hang")
                    ));
            }
            else if (ContainsAny(message, "van phong", "toa nha", "tru so", "cong ty"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("văn phòng") ||
                        p.PropertyType.TypeName.ToLower().Contains("van phong") ||
                        p.PropertyType.TypeName.ToLower().Contains("tòa nhà") ||
                        p.PropertyType.TypeName.ToLower().Contains("toa nha") ||
                        p.Title.ToLower().Contains("văn phòng") ||
                        p.Title.ToLower().Contains("van phong") ||
                        p.Title.ToLower().Contains("tòa nhà") ||
                        p.Title.ToLower().Contains("toa nha") ||
                        p.Title.ToLower().Contains("trụ sở") ||
                        p.Title.ToLower().Contains("tru so")
                    ));
            }
            else if (ContainsAny(message, "nha", "nha pho", "nha rieng", "nha nguyen can"))
            {
                query = query.Where(p =>
                    p.PropertyType != null &&
                    (
                        p.PropertyType.TypeName.ToLower().Contains("nhà") ||
                        p.PropertyType.TypeName.ToLower().Contains("nha") ||
                        p.Title.ToLower().Contains("nhà") ||
                        p.Title.ToLower().Contains("nha")
                    ));
            }

            return query;
        }

        private static IQueryable<Property> ApplyLocationFilter(IQueryable<Property> query, string message)
        {
            if (ContainsAny(message, "nha trang", "nhatrang"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("nha trang") ||
                         p.Ward.Area.AreaName.ToLower().Contains("nha trang")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("nha trang") ||
                         p.AddressDetail.ToLower().Contains("nhatrang")
                     )));
            }
            else if (ContainsAny(message, "cam ranh"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     p.Ward.Area.AreaName.ToLower().Contains("cam ranh")) ||
                    (p.AddressDetail != null && p.AddressDetail.ToLower().Contains("cam ranh")));
            }
            else if (ContainsAny(message, "cam lam"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("cam lâm") ||
                         p.Ward.Area.AreaName.ToLower().Contains("cam lam")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("cam lâm") ||
                         p.AddressDetail.ToLower().Contains("cam lam")
                     )));
            }
            else if (ContainsAny(message, "ninh hoa"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("ninh hòa") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh hoa")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("ninh hòa") ||
                         p.AddressDetail.ToLower().Contains("ninh hoa")
                     )));
            }
            else if (ContainsAny(message, "van ninh"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("vạn ninh") ||
                         p.Ward.Area.AreaName.ToLower().Contains("van ninh")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("vạn ninh") ||
                         p.AddressDetail.ToLower().Contains("van ninh")
                     )));
            }
            else if (ContainsAny(message, "dien khanh"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("diên khánh") ||
                         p.Ward.Area.AreaName.ToLower().Contains("dien khanh")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("diên khánh") ||
                         p.AddressDetail.ToLower().Contains("dien khanh")
                     )));
            }
            else if (ContainsAny(message, "khanh vinh"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("khánh vĩnh") ||
                         p.Ward.Area.AreaName.ToLower().Contains("khanh vinh")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("khánh vĩnh") ||
                         p.AddressDetail.ToLower().Contains("khanh vinh")
                     )));
            }
            else if (ContainsAny(message, "khanh son"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("khánh sơn") ||
                         p.Ward.Area.AreaName.ToLower().Contains("khanh son")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("khánh sơn") ||
                         p.AddressDetail.ToLower().Contains("khanh son")
                     )));
            }
            else if (ContainsAny(message, "phan rang", "ninh thuan", "ninh hai", "ninh phuoc", "thuan nam", "thuan bac", "ninh son", "bac ai", "ninh chu", "vinh hy", "ca na"))
            {
                query = query.Where(p =>
                    (p.Ward != null && p.Ward.Area != null &&
                     (
                         p.Ward.Area.AreaName.ToLower().Contains("phan rang") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh thuận") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh thuan") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh hải") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh hai") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh phước") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh phuoc") ||
                         p.Ward.Area.AreaName.ToLower().Contains("thuận nam") ||
                         p.Ward.Area.AreaName.ToLower().Contains("thuan nam") ||
                         p.Ward.Area.AreaName.ToLower().Contains("thuận bắc") ||
                         p.Ward.Area.AreaName.ToLower().Contains("thuan bac") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh sơn") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh son") ||
                         p.Ward.Area.AreaName.ToLower().Contains("bác ái") ||
                         p.Ward.Area.AreaName.ToLower().Contains("bac ai") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh chữ") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ninh chu") ||
                         p.Ward.Area.AreaName.ToLower().Contains("vĩnh hy") ||
                         p.Ward.Area.AreaName.ToLower().Contains("vinh hy") ||
                         p.Ward.Area.AreaName.ToLower().Contains("cà ná") ||
                         p.Ward.Area.AreaName.ToLower().Contains("ca na")
                     )) ||
                    (p.AddressDetail != null &&
                     (
                         p.AddressDetail.ToLower().Contains("phan rang") ||
                         p.AddressDetail.ToLower().Contains("ninh thuận") ||
                         p.AddressDetail.ToLower().Contains("ninh thuan") ||
                         p.AddressDetail.ToLower().Contains("ninh hải") ||
                         p.AddressDetail.ToLower().Contains("ninh hai") ||
                         p.AddressDetail.ToLower().Contains("ninh phước") ||
                         p.AddressDetail.ToLower().Contains("ninh phuoc") ||
                         p.AddressDetail.ToLower().Contains("thuận nam") ||
                         p.AddressDetail.ToLower().Contains("thuan nam") ||
                         p.AddressDetail.ToLower().Contains("thuận bắc") ||
                         p.AddressDetail.ToLower().Contains("thuan bac") ||
                         p.AddressDetail.ToLower().Contains("ninh sơn") ||
                         p.AddressDetail.ToLower().Contains("ninh son") ||
                         p.AddressDetail.ToLower().Contains("bác ái") ||
                         p.AddressDetail.ToLower().Contains("bac ai") ||
                         p.AddressDetail.ToLower().Contains("ninh chữ") ||
                         p.AddressDetail.ToLower().Contains("ninh chu") ||
                         p.AddressDetail.ToLower().Contains("vĩnh hy") ||
                         p.AddressDetail.ToLower().Contains("vinh hy") ||
                         p.AddressDetail.ToLower().Contains("cà ná") ||
                         p.AddressDetail.ToLower().Contains("ca na")
                     )));
            }

            return query;
        }

        private static IQueryable<Property> ApplyAreaSizeFilter(IQueryable<Property> query, string message)
        {
            decimal? minArea = null;
            decimal? maxArea = null;

            Match rangeMatch = Regex.Match(
                message,
                @"(?:tu)\s*(\d+(?:[.,]\d+)?)\s*(?:m2|m²|met vuong)?\s*(?:den|-)\s*(\d+(?:[.,]\d+)?)\s*(?:m2|m²|met vuong)");

            if (rangeMatch.Success)
            {
                minArea = ParseDecimal(rangeMatch.Groups[1].Value);
                maxArea = ParseDecimal(rangeMatch.Groups[2].Value);
            }
            else
            {
                Match maxAreaMatch = Regex.Match(
                    message,
                    @"(?:duoi|nho hon|toi da)\s*(\d+(?:[.,]\d+)?)\s*(?:m2|m²|met vuong)");

                Match minAreaMatch = Regex.Match(
                    message,
                    @"(?:tren|tu|lon hon|toi thieu)\s*(\d+(?:[.,]\d+)?)\s*(?:m2|m²|met vuong)");

                if (maxAreaMatch.Success)
                {
                    maxArea = ParseDecimal(maxAreaMatch.Groups[1].Value);
                }

                if (minAreaMatch.Success)
                {
                    minArea = ParseDecimal(minAreaMatch.Groups[1].Value);
                }
            }

            if (minArea.HasValue && minArea.Value > 0)
            {
                query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value >= minArea.Value);
            }

            if (maxArea.HasValue && maxArea.Value > 0)
            {
                query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value <= maxArea.Value);
            }

            return query;
        }

        private static IQueryable<Property> ApplyPriceFilter(IQueryable<Property> query, string message)
        {
            (decimal? minPrice, decimal? maxPrice) = ExtractPriceRange(message);

            if (minPrice.HasValue && minPrice.Value > 0)
            {
                query = query.Where(p => p.Price.HasValue && p.Price.Value >= minPrice.Value);
            }

            if (maxPrice.HasValue && maxPrice.Value > 0)
            {
                query = query.Where(p => p.Price.HasValue && p.Price.Value <= maxPrice.Value);
            }

            return query;
        }

        // =====================================================
        // 3. XỬ LÝ GIÁ / NGÂN SÁCH
        // =====================================================
        private static (decimal? minPrice, decimal? maxPrice) ExtractPriceRange(string message)
        {
            decimal? minPrice = null;
            decimal? maxPrice = null;

            Match rangeMatch = Regex.Match(
                message,
                @"(?:tu)\s*(\d+(?:[.,]\d+)?)\s*(ty|ti|trieu)?\s*(?:den|-)\s*(\d+(?:[.,]\d+)?)\s*(ty|ti|trieu)");

            if (rangeMatch.Success)
            {
                string firstNumber = rangeMatch.Groups[1].Value;
                string firstUnit = rangeMatch.Groups[2].Value;
                string secondNumber = rangeMatch.Groups[3].Value;
                string secondUnit = rangeMatch.Groups[4].Value;

                if (string.IsNullOrWhiteSpace(firstUnit))
                {
                    firstUnit = secondUnit;
                }

                minPrice = ConvertToMoney(firstNumber, firstUnit);
                maxPrice = ConvertToMoney(secondNumber, secondUnit);

                return (minPrice, maxPrice);
            }

            Match underMatch = Regex.Match(
                message,
                @"(?:duoi|toi da|tam|khoang|ngan sach)\s*(\d+(?:[.,]\d+)?)\s*(ty|ti|trieu)");

            if (underMatch.Success)
            {
                maxPrice = ConvertToMoney(underMatch.Groups[1].Value, underMatch.Groups[2].Value);
                return (minPrice, maxPrice);
            }

            Match aboveMatch = Regex.Match(
                message,
                @"(?:tren|tu|toi thieu)\s*(\d+(?:[.,]\d+)?)\s*(ty|ti|trieu)");

            if (aboveMatch.Success)
            {
                minPrice = ConvertToMoney(aboveMatch.Groups[1].Value, aboveMatch.Groups[2].Value);
                return (minPrice, maxPrice);
            }

            Match singleMatch = Regex.Match(
                message,
                @"(\d+(?:[.,]\d+)?)\s*(ty|ti|trieu)");

            if (singleMatch.Success && ContainsAny(message, "tam", "khoang", "ngan sach", "duoi", "toi da"))
            {
                maxPrice = ConvertToMoney(singleMatch.Groups[1].Value, singleMatch.Groups[2].Value);
            }

            return (minPrice, maxPrice);
        }

        private static decimal ConvertToMoney(string number, string unit)
        {
            decimal value = ParseDecimal(number);
            string normalizedUnit = NormalizeText(unit);

            if (normalizedUnit.Contains("ty") || normalizedUnit.Contains("ti"))
            {
                return value * 1_000_000_000M;
            }

            if (normalizedUnit.Contains("trieu"))
            {
                return value * 1_000_000M;
            }

            return value;
        }

        private static decimal ParseDecimal(string value)
        {
            value = (value ?? "").Replace(",", ".");

            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal result))
            {
                return result;
            }

            return 0;
        }

        // =====================================================
        // 4. RAG DATA TỪ STATICPAGES
        // =====================================================
        private async Task<string> GetRagDataAsync()
        {
            string? content = await _context.StaticPages
                .AsNoTracking()
                .Where(s => s.PageKey == "ai_knowledge_base")
                .Select(s => s.Content)
                .FirstOrDefaultAsync();

            if (string.IsNullOrWhiteSpace(content))
            {
                return """
                Chưa có dữ liệu đào tạo nội bộ trong trang AI RAG.
                Khi gặp câu hỏi về chính sách riêng của sàn, gói VIP, phí dịch vụ, quy định nội bộ, hãy trả lời thận trọng và hướng dẫn khách liên hệ bộ phận hỗ trợ.
                """;
            }

            content = StripHtml(content);

            if (content.Length > 20000)
            {
                content = content.Substring(0, 20000);
            }

            return content;
        }

        private static string BuildSystemKnowledge()
        {
            return """
            [KIẾN THỨC HỆ THỐNG CỐ ĐỊNH]

            1. Vai trò website:
            - Website BĐS Khánh Hòa hỗ trợ xem tin, tìm kiếm, lọc BĐS, xem chi tiết tin, xem dự án, đăng tin, quản lý tin, lưu tin yêu thích, bình luận, báo cáo vi phạm, gửi yêu cầu tư vấn, đặt lịch xem BĐS và chat trực tiếp.
            - Website tập trung vào khu vực Khánh Hòa mới, bao gồm Khánh Hòa cũ và Ninh Thuận cũ nếu hệ thống có dữ liệu tin đăng.

            2. Vai trò người dùng:
            - Guest: xem tin công khai, tìm kiếm, lọc, xem chi tiết, xem dự án, dùng chatbot ở mức cơ bản.
            - Member: đăng tin, quản lý tin cá nhân, lưu yêu thích, bình luận, báo cáo vi phạm, gửi yêu cầu tư vấn, đặt lịch xem, chat với người đăng tin.
            - Staff: hỗ trợ kiểm duyệt tin, xử lý báo cáo, quản lý bình luận, rà soát nội dung.
            - Admin: quản lý toàn bộ hệ thống, người dùng, phân quyền, danh mục, tin rao, dự án, bài viết, banner, thông báo, giao dịch, voucher, chatbot AI và nhật ký.

            3. Nguyên tắc tư vấn BĐS:
            - Trả lời đúng trọng tâm, dễ hiểu, có cấu trúc.
            - Không cam kết chắc chắn lợi nhuận, pháp lý, quy hoạch, vay ngân hàng nếu không có dữ liệu xác thực.
            - Khi khách hỏi mua/thuê, cần hỏi hoặc suy luận các tiêu chí: khu vực, ngân sách, diện tích, loại hình, mục đích, pháp lý, thời gian xem.
            - Khi khách hỏi pháp lý, chỉ tư vấn thông tin kiểm tra cơ bản, khuyên khách xác minh giấy tờ, quy hoạch, tranh chấp và công chứng với cơ quan/chuyên gia phù hợp.
            - Khi khách hỏi vay vốn, chỉ hỗ trợ tính tham khảo, không thay thế ngân hàng.

            4. Nguyên tắc không bịa:
            - Không tự tạo tên dự án, giá, địa chỉ, số điện thoại, chính sách hoặc tin đăng nếu dữ liệu không có trong ngữ cảnh.
            - Nếu thiếu thông tin, hãy nói rõ thiếu thông tin và hỏi lại ngắn gọn.
            - Không nói “chắc chắn”, “đảm bảo sinh lời”, “pháp lý 100% an toàn” nếu chưa có nguồn xác minh.

            5. Cách trả lời:
            - Ưu tiên tiếng Việt.
            - Giọng văn thân thiện, chuyên nghiệp, giống nhân viên tư vấn BĐS.
            - Câu trả lời nên ngắn gọn nhưng đủ ý.
            - Có thể dùng markdown: tiêu đề nhỏ, gạch đầu dòng, in đậm ý quan trọng.
            """;
        }

        private static string BuildCurrentPageContext(string? pageContext)
        {
            if (string.IsNullOrWhiteSpace(pageContext))
            {
                return """
                [NGỮ CẢNH TRANG HIỆN TẠI]
                Người dùng không gửi ngữ cảnh trang chi tiết BĐS.
                """;
            }

            string clean = StripHtml(pageContext);

            if (clean.Length > 6000)
            {
                clean = clean.Substring(0, 6000);
            }

            return $"""
            [NGỮ CẢNH TRANG KHÁCH ĐANG XEM]
            {clean}
            """;
        }

        private static string BuildSuggestedPropertiesContext(List<Property> properties, bool shouldSearch)
        {
            if (!shouldSearch)
            {
                return """
                [DANH SÁCH BĐS GỢI Ý]
                Người dùng chưa yêu cầu tìm BĐS.
                Không được tự giới thiệu danh sách tin đăng.
                """;
            }

            if (!properties.Any())
            {
                return """
                [DANH SÁCH BĐS GỢI Ý]
                Người dùng có nhu cầu tìm BĐS nhưng hệ thống chưa tìm thấy tin phù hợp theo bộ lọc hiện tại.
                Hãy xin thêm khu vực, ngân sách, diện tích, loại hình hoặc mời khách để lại thông tin liên hệ.
                """;
            }

            StringBuilder builder = new();

            builder.AppendLine("[DANH SÁCH BĐS PHÙ HỢP TỪ CƠ SỞ DỮ LIỆU]");
            builder.AppendLine("Chỉ dùng danh sách này khi khách đang muốn tìm BĐS. Giới thiệu tối đa 3 lựa chọn nổi bật, ngắn gọn, dễ hiểu. Có thể nhắc khách bấm thẻ bên dưới để xem chi tiết.");

            foreach (Property p in properties)
            {
                builder.AppendLine(
                    $"- Tiêu đề: {p.Title} | Giá: {FormatPrice(p.Price)} | Diện tích: {(p.AreaSize.HasValue ? $"{p.AreaSize.Value:0.##} m²" : "Chưa rõ")} | Vị trí: {BuildLocationText(p)} | Loại: {p.PropertyType?.TypeName ?? "Chưa rõ"} | Link: /Property/Details/{p.PropertyID}");
            }

            return builder.ToString();
        }

        private static string BuildPrompt(
            string userMessage,
            string normalizedUserMessage,
            string systemKnowledge,
            string ragData,
            string currentPageContext,
            string propertySuggestionContext,
            ChatIntent intent)
        {
            return $"""
            Bạn là Trợ lý AI của website BĐS Khánh Hòa.

            {systemKnowledge}

            [Ý ĐỊNH HỆ THỐNG ĐÃ NHẬN DIỆN]
            - Intent: {intent.Name}
            - Đang xem trang chi tiết BĐS: {(intent.IsViewingProperty ? "Có" : "Không")}
            - Đang hỏi về tin hiện tại: {(intent.IsAskingAboutCurrentProperty ? "Có" : "Không")}
            - Được phép gợi ý danh sách BĐS: {(intent.ShouldSearchProperties ? "Có" : "Không")}
            - Nhu cầu mua: {(intent.WantsBuy ? "Có" : "Không")}
            - Nhu cầu thuê: {(intent.WantsRent ? "Có" : "Không")}

            [DỮ LIỆU HUẤN LUYỆN NỘI BỘ DO ADMIN CUNG CẤP]
            {ragData}

            {currentPageContext}

            {propertySuggestionContext}

            [QUY TẮC TRẢ LỜI BẮT BUỘC]
            1. Trả lời đúng câu hỏi của khách, không lan man.
            2. Không bịa dữ liệu ngoài những gì có trong dữ liệu nội bộ, ngữ cảnh trang và danh sách BĐS.
            3. Nếu câu hỏi liên quan pháp lý/quy hoạch/hợp đồng:
               - Chỉ tư vấn ở mức tham khảo.
               - Nhắc khách kiểm tra giấy tờ, quy hoạch, tranh chấp, công chứng hoặc hỏi cơ quan/chuyên gia có thẩm quyền.
            4. Nếu câu hỏi liên quan vay vốn/lãi suất:
               - Chỉ tính tham khảo.
               - Không cam kết điều kiện duyệt vay.
            5. Nếu "Được phép gợi ý danh sách BĐS" là "Không":
               - Không tự giới thiệu tin BĐS khác.
               - Không nói “mình gợi ý các căn sau”.
            6. Nếu khách đang xem tin cụ thể:
               - Tập trung phân tích tin đang xem.
               - Có thể gợi ý: kiểm tra pháp lý, liên hệ người đăng, đặt lịch xem thực tế, so sánh giá khu vực nếu có dữ liệu.
            7. Nếu khách muốn tìm BĐS:
               - Dựa vào danh sách BĐS phù hợp nếu có.
               - Nếu không có tin phù hợp, hỏi lại tiêu chí còn thiếu.
            8. Không dùng giọng quá máy móc. Trả lời như nhân viên tư vấn BĐS chuyên nghiệp.
            9. Định dạng dễ đọc bằng markdown.
            10. Không nhắc đến “prompt”, “model”, “dữ liệu huấn luyện”, “RAG”, “context hệ thống” với khách.

            [CÂU HỎI GỐC CỦA KHÁCH]
            {userMessage}

            [CÂU HỎI ĐÃ CHUẨN HÓA KHÔNG DẤU]
            {normalizedUserMessage}

            Hãy trả lời bằng tiếng Việt, đúng trọng tâm, đầy đủ nhưng không dài dòng.
            """;
        }

        // =====================================================
        // 5. GỌI GEMINI - CÓ RETRY + MODEL DỰ PHÒNG
        // =====================================================
        private async Task<string> CallGeminiWithRetryAndFallbackAsync(string prompt)
        {
            string? apiKey = _config["GeminiApiSettings:ApiKey"];
            string baseUrl = _config["GeminiApiSettings:BaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta";

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("Chưa cấu hình GeminiApiSettings:ApiKey trong appsettings.json.");
                return "";
            }

            baseUrl = baseUrl.TrimEnd('/');

            List<string> models = new()
            {
                _config["GeminiApiSettings:Model"] ?? "gemini-2.5-flash",
                "gemini-2.5-flash-lite",
                "gemini-2.0-flash"
            };

            models = models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string model in models)
            {
                string result = await CallGeminiModelWithRetryAsync(baseUrl, apiKey, model, prompt);

                if (!string.IsNullOrWhiteSpace(result))
                {
                    return result;
                }
            }

            return "";
        }

        private async Task<string> CallGeminiModelWithRetryAsync(string baseUrl, string apiKey, string model, string prompt)
        {
            const int maxAttempts = 3;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                GeminiCallResult result = await CallGeminiOnceAsync(baseUrl, apiKey, model, prompt);

                if (result.Success && !string.IsNullOrWhiteSpace(result.Text))
                {
                    return result.Text;
                }

                if (!ShouldRetryGeminiStatus(result.StatusCode))
                {
                    return "";
                }

                if (attempt < maxAttempts)
                {
                    int delayMs = attempt switch
                    {
                        1 => 700,
                        2 => 1500,
                        _ => 2500
                    };

                    await Task.Delay(delayMs);
                }
            }

            return "";
        }

        private async Task<GeminiCallResult> CallGeminiOnceAsync(string baseUrl, string apiKey, string model, string prompt)
        {
            string endpoint = $"{baseUrl}/models/{model}:generateContent?key={apiKey}";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[]
                        {
                            new
                            {
                                text = prompt
                            }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.35,
                    topP = 0.85,
                    topK = 40,
                    maxOutputTokens = 1800
                }
            };

            try
            {
                using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(endpoint, payload);

                string raw = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                        response.StatusCode == HttpStatusCode.TooManyRequests ||
                        response.StatusCode == HttpStatusCode.RequestTimeout)
                    {
                        _logger.LogWarning(
                            "Gemini model {Model} đang quá tải hoặc bị giới hạn. Status: {StatusCode}.",
                            model,
                            response.StatusCode);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Gemini API lỗi. Model: {Model}. Status: {StatusCode}. Body: {Body}",
                            model,
                            response.StatusCode,
                            raw);
                    }

                    return new GeminiCallResult
                    {
                        Success = false,
                        StatusCode = response.StatusCode,
                        Text = ""
                    };
                }

                string text = ExtractGeminiText(raw);

                return new GeminiCallResult
                {
                    Success = true,
                    StatusCode = response.StatusCode,
                    Text = text
                };
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogWarning(ex, "Gemini API timeout. Model: {Model}", model);

                return new GeminiCallResult
                {
                    Success = false,
                    StatusCode = HttpStatusCode.RequestTimeout,
                    Text = ""
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gọi Gemini API. Model: {Model}", model);

                return new GeminiCallResult
                {
                    Success = false,
                    StatusCode = null,
                    Text = ""
                };
            }
        }

        private static bool ShouldRetryGeminiStatus(HttpStatusCode? statusCode)
        {
            if (!statusCode.HasValue)
            {
                return false;
            }

            return statusCode.Value == HttpStatusCode.ServiceUnavailable ||
                   statusCode.Value == HttpStatusCode.TooManyRequests ||
                   statusCode.Value == HttpStatusCode.RequestTimeout ||
                   statusCode.Value == HttpStatusCode.InternalServerError ||
                   statusCode.Value == HttpStatusCode.BadGateway ||
                   statusCode.Value == HttpStatusCode.GatewayTimeout;
        }

        private static string ExtractGeminiText(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return "";
            }

            using JsonDocument doc = JsonDocument.Parse(rawJson);

            if (!doc.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() == 0)
            {
                return "";
            }

            JsonElement firstCandidate = candidates[0];

            if (!firstCandidate.TryGetProperty("content", out JsonElement content))
            {
                return "";
            }

            if (!content.TryGetProperty("parts", out JsonElement parts) ||
                parts.ValueKind != JsonValueKind.Array ||
                parts.GetArrayLength() == 0)
            {
                return "";
            }

            StringBuilder builder = new();

            foreach (JsonElement part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out JsonElement textElement))
                {
                    string? text = textElement.GetString();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        builder.AppendLine(text);
                    }
                }
            }

            return builder.ToString().Trim();
        }

        // =====================================================
        // 6. FALLBACK THÔNG MINH KHI GEMINI LỖI / QUÁ TẢI
        // =====================================================
        private static string BuildSmartFallbackMessage(ChatIntent intent, List<Property> suggestedProperties, string? pageContext)
        {
            if (intent.Name == "PropertySearch")
            {
                if (suggestedProperties.Any())
                {
                    StringBuilder builder = new();

                    builder.AppendLine("Mình tìm được một số lựa chọn có vẻ phù hợp với nhu cầu của bạn:");
                    builder.AppendLine();

                    int index = 1;

                    foreach (Property p in suggestedProperties.Take(3))
                    {
                        builder.AppendLine($"{index}. **{p.Title}**");
                        builder.AppendLine($"   - **Giá:** {FormatPrice(p.Price)}");
                        builder.AppendLine($"   - **Diện tích:** {(p.AreaSize.HasValue ? $"{p.AreaSize.Value:0.##} m²" : "Chưa cập nhật")}");
                        builder.AppendLine($"   - **Vị trí:** {BuildLocationText(p)}");
                        builder.AppendLine();

                        index++;
                    }

                    builder.AppendLine("Bạn có thể bấm vào thẻ tin bên dưới để xem chi tiết, hình ảnh và liên hệ người đăng.");
                    builder.AppendLine();
                    builder.AppendLine("Bạn muốn ưu tiên lựa chọn **giá thấp hơn**, **gần trung tâm hơn**, hay **diện tích rộng hơn** ạ?");

                    return builder.ToString();
                }

                return """
                Mình chưa tìm thấy tin BĐS thật sự phù hợp theo tiêu chí hiện tại.

                Bạn cho mình thêm 3 thông tin này để lọc chính xác hơn nhé:

                - **Khu vực** bạn muốn tìm.
                - **Ngân sách** khoảng bao nhiêu.
                - Bạn muốn **mua hay thuê**, loại **nhà/đất/căn hộ/mặt bằng**.

                Bạn đang quan tâm khu vực Khánh Hòa cũ như Nha Trang, Cam Ranh, Cam Lâm hay khu vực Ninh Thuận cũ như Phan Rang, Ninh Hải, Ninh Phước ạ?
                """;
            }

            if (intent.Name == "CurrentPropertyAdvice")
            {
                if (!string.IsNullOrWhiteSpace(pageContext))
                {
                    return """
                    Mình đã nhận được câu hỏi về bất động sản bạn đang xem.

                    Để đánh giá căn/tin này an toàn hơn, bạn nên kiểm tra theo các điểm chính:

                    - **Giá:** so sánh thêm với các tin cùng khu vực.
                    - **Diện tích:** kiểm tra diện tích hiển thị và diện tích trên giấy tờ.
                    - **Vị trí:** nên đi xem thực tế để đánh giá đường vào, tiện ích và môi trường xung quanh.
                    - **Pháp lý:** cần xem sổ đỏ/sổ hồng bản gốc, quy hoạch, tranh chấp và tình trạng thế chấp nếu có.
                    - **Mục đích mua:** mua để ở sẽ khác với mua đầu tư hoặc cho thuê.

                    Nếu bạn thấy tin này phù hợp, nên bấm **Đặt lịch xem nhà** hoặc **Yêu cầu tư vấn miễn phí** để được hỗ trợ kiểm tra kỹ hơn.

                    Bạn muốn mình phân tích tin này theo hướng **mua để ở** hay **mua để đầu tư** ạ?
                    """;
                }

                return """
                Mình có thể hỗ trợ phân tích bất động sản bạn đang quan tâm.

                Bạn gửi thêm giúp mình thông tin về **giá**, **diện tích**, **vị trí** và **pháp lý** của tin đó, mình sẽ đánh giá sơ bộ cho bạn dễ quyết định hơn.
                """;
            }

            if (intent.Name == "LegalAdvice")
            {
                return """
                Về pháp lý bất động sản, bạn nên kiểm tra kỹ trước khi đặt cọc hoặc giao dịch.

                Các điểm quan trọng gồm:

                - **Sổ đỏ/sổ hồng bản gốc**.
                - **Chủ sở hữu** có đúng là người bán hoặc người được ủy quyền hợp pháp không.
                - **Diện tích trên sổ** và diện tích thực tế.
                - **Loại đất**, thời hạn sử dụng đất.
                - **Quy hoạch**, lộ giới, hành lang biển/sông suối nếu có.
                - **Tranh chấp**, thế chấp ngân hàng, kê biên hoặc ngăn chặn giao dịch.
                - **Hợp đồng đặt cọc/chuyển nhượng** nên rõ ràng và nên công chứng/chứng thực khi cần.

                Mình chỉ tư vấn ở mức tham khảo. Với giao dịch giá trị lớn, bạn nên xác minh tại cơ quan chức năng hoặc hỏi luật sư/công chứng viên.

                Bạn đang muốn kiểm tra pháp lý cho **nhà**, **đất**, hay **dự án** ạ?
                """;
            }

            if (intent.Name == "LoanAdvice")
            {
                return """
                Mình có thể hỗ trợ tính khoản vay mua bất động sản ở mức tham khảo.

                Bạn cần chuẩn bị các thông tin:

                - **Giá trị bất động sản**.
                - **Số tiền tự có**.
                - **Số tiền muốn vay**.
                - **Thời hạn vay**.
                - **Lãi suất dự kiến** nếu đã có.

                Lưu ý: lãi suất và điều kiện duyệt vay thay đổi theo từng ngân hàng và từng thời điểm, nên kết quả chỉ dùng để tham khảo.

                Bạn dự kiến mua bất động sản giá bao nhiêu và muốn vay khoảng bao nhiêu ạ?
                """;
            }

            if (intent.Name == "PackagePolicy")
            {
                return """
                Hệ thống có thể hỗ trợ các gói đăng tin như **Tin Thường**, **VIP Đồng**, **VIP Bạc**, **VIP Vàng** và **VIP Kim Cương**.

                Cách hiểu chung:

                - **Tin Thường:** phù hợp khi chỉ cần đăng cơ bản.
                - **VIP Đồng:** nổi bật hơn Tin Thường, chi phí tiết kiệm.
                - **VIP Bạc:** cân bằng giữa chi phí và hiệu quả hiển thị.
                - **VIP Vàng:** phù hợp tin cần tiếp cận khách tốt hơn.
                - **VIP Kim Cương:** gói cao nhất, tăng độ nổi bật mạnh nhất.

                Gói VIP giúp tăng khả năng hiển thị và cơ hội tiếp cận khách hàng, nhưng không cam kết chắc chắn bán/cho thuê thành công.

                Bạn muốn hỏi về **giá gói**, **thời hạn**, hay **nên chọn gói nào cho tin của bạn** ạ?
                """;
            }

            if (intent.Name == "WebsiteGuide")
            {
                return """
                Mình có thể hướng dẫn bạn sử dụng website BĐS Khánh Hòa.

                Một số thao tác phổ biến:

                - **Tìm kiếm BĐS:** vào mục Mua bán/Cho thuê rồi dùng bộ lọc khu vực, giá, diện tích, loại BĐS.
                - **Đăng tin:** đăng nhập tài khoản, bấm Đăng tin, nhập đầy đủ thông tin và gửi duyệt.
                - **Đặt lịch xem:** vào trang chi tiết tin, bấm Đặt lịch xem nhà.
                - **Yêu cầu tư vấn:** nhập họ tên, số điện thoại và nội dung cần tư vấn.
                - **Lưu tin:** bấm biểu tượng yêu thích để xem lại sau.
                - **Báo cáo vi phạm:** dùng khi phát hiện tin sai giá, sai hình ảnh, nghi ngờ lừa đảo.

                Bạn muốn mình hướng dẫn cụ thể thao tác nào trước ạ?
                """;
            }

            return """
            Mình là trợ lý AI của BĐS Khánh Hòa.

            Bạn có thể hỏi mình về:

            - Tìm **nhà, đất, căn hộ, mặt bằng** theo khu vực và ngân sách.
            - Phân tích tin BĐS bạn đang xem.
            - Hướng dẫn **đăng tin**, **đặt lịch xem nhà**, **gửi yêu cầu tư vấn**.
            - Tư vấn **gói VIP**, thanh toán, voucher.
            - Kiểm tra **pháp lý BĐS** ở mức tham khảo.
            - So sánh khu vực Khánh Hòa cũ và Ninh Thuận cũ.

            Bạn đang muốn tìm mua, thuê hay cần phân tích một tin cụ thể ạ?
            """;
        }

        private static string CleanBotMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "";
            }

            message = message.Trim();

            message = message.Replace("```markdown", "", StringComparison.OrdinalIgnoreCase)
                             .Replace("```", "");

            return message.Trim();
        }

        // =====================================================
        // 7. LƯU LỊCH SỬ CHAT
        // =====================================================
        private async Task SaveChatLogAsync(ChatRequest req, string botMessage)
        {
            try
            {
                ChatLogs log = new ChatLogs
                {
                    UserID = req.UserId > 0 ? req.UserId : null,
                    UserMessage = req.Message,
                    BotResponse = botMessage,
                    CreatedAt = DateTime.Now
                };

                _context.ChatLogs.Add(log);
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không lưu được ChatLogs.");
            }
        }

        // =====================================================
        // 8. HELPER
        // =====================================================
        private static string FormatPrice(decimal? price)
        {
            if (!price.HasValue || price.Value <= 0)
            {
                return "Thỏa thuận";
            }

            decimal value = price.Value;

            if (value >= 1_000_000_000M)
            {
                decimal billion = value / 1_000_000_000M;
                return $"{billion:0.##} tỷ";
            }

            if (value >= 1_000_000M)
            {
                decimal million = value / 1_000_000M;
                return $"{million:0.##} triệu";
            }

            return $"{value:N0} đ";
        }

        private static string BuildLocationText(Property p)
        {
            List<string> parts = new();

            if (p.Ward != null && !string.IsNullOrWhiteSpace(p.Ward.WardName))
            {
                parts.Add(p.Ward.WardName);
            }

            if (p.Ward?.Area != null && !string.IsNullOrWhiteSpace(p.Ward.Area.AreaName))
            {
                parts.Add(p.Ward.Area.AreaName);
            }

            if (!parts.Any() && !string.IsNullOrWhiteSpace(p.AddressDetail))
            {
                parts.Add(p.AddressDetail);
            }

            return parts.Any() ? string.Join(", ", parts) : "Chưa cập nhật vị trí";
        }

        private static bool ContainsAny(string source, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            foreach (string keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                {
                    continue;
                }

                string normalizedKeyword = NormalizeText(keyword);

                if (source.Contains(normalizedKeyword))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeText(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "";
            }

            string normalized = input.Trim().ToLowerInvariant();

            normalized = normalized.Replace("đ", "d").Replace("Đ", "d");

            string formD = normalized.Normalize(NormalizationForm.FormD);
            StringBuilder builder = new();

            foreach (char c in formD)
            {
                UnicodeCategory unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);

                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            string result = builder.ToString().Normalize(NormalizationForm.FormC);

            result = Regex.Replace(result, @"\s+", " ").Trim();

            return result;
        }

        private static string StripHtml(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "";
            }

            string text = Regex.Replace(input, "<.*?>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            return text;
        }

        private class ChatIntent
        {
            public string Name { get; set; } = "General";

            public bool IsViewingProperty { get; set; }

            public bool IsAskingAboutCurrentProperty { get; set; }

            public bool ShouldSearchProperties { get; set; }

            public bool WantsBuy { get; set; }

            public bool WantsRent { get; set; }

            public bool WantsOtherOptions { get; set; }
        }

        private class GeminiCallResult
        {
            public bool Success { get; set; }

            public HttpStatusCode? StatusCode { get; set; }

            public string Text { get; set; } = "";
        }
    }
}
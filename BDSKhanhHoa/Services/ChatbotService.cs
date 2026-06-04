using BDSKhanhHoa.Data;
using BDSKhanhHoa.Models;
using BDSKhanhHoa.Services.AI;
using BDSKhanhHoa.ViewModels;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BDSKhanhHoa.Services
{
    /// <summary>
    /// ChatbotService bản strict/clarify-first.
    /// Mục tiêu: không tự đề xuất tin BĐS khi câu hỏi còn mơ hồ; muốn gợi ý tin phải đủ
    /// mua/thuê + loại BĐS + khu vực + ngân sách. Câu hỏi kiến thức/pháp lý/vay/quy trình
    /// đi theo route tư vấn, không chạy SQL. Có fallback để AI không bịa khi không biết.
    /// </summary>
    public class ChatbotService
    {
        private readonly ApplicationDbContext _context;
        private readonly IAIModelClient _aiClient;
        private readonly ILogger<ChatbotService> _logger;
        private static readonly CultureInfo Vi = new("vi-VN");
        private const int MaxCards = 5;
        private const int ThinkingDelayMilliseconds = 1200; // Không nên để 10000ms vì dễ làm request chậm/crash khi test nhiều câu. Nên tạo hiệu ứng typing ở frontend nếu muốn quay lâu hơn.

        public ChatbotService(ApplicationDbContext context, IAIModelClient aiClient, ILogger<ChatbotService> logger)
        {
            _context = context;
            _aiClient = aiClient;
            _logger = logger;
        }

        public async Task<ChatResponse> ProcessChatAsync(ChatRequest req)
        {
            string original = (req.Message ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(original))
                return BuildResponse(Welcome(), "Empty", "General", "Start", req.SessionId ?? "", new(), false, new(), Replies("General"));

            PageInfo page = BuildPageInfo(req);
            AIChatSession session = await GetOrCreateSessionAsync(req, page);
            List<AIChatMessage> history = await LoadRecentMessagesAsync(session.SessionID);
            Dictionary<string, string> slots = await LoadSlotsAsync(session.SessionID);

            string normalized = ResolveShortChoice(Normalize(original), history);
            await ApplyThinkingDelayAsync(normalized);
            Scenario scenario = DetectScenario(normalized, page, session, slots);
            ResetSlotsForNewScenario(slots, scenario, session.Scenario ?? "General", normalized);
            ExtractSlots(normalized, original, slots, scenario, page, history);
            SlotPlan plan = BuildSlotPlan(scenario, slots, normalized);
            Route route = DecideRoute(scenario, slots, plan, normalized, page);

            List<Property> properties = new();
            string answer;
            string trace;

            switch (route)
            {
                case Route.Refuse:
                    answer = UnsafeAnswer();
                    trace = "Guardrail:Unsafe";
                    break;
                case Route.OffTopic:
                    answer = OffTopicAnswer(normalized);
                    trace = "Guardrail:OffTopic";
                    break;
                case Route.Clarify:
                    answer = ClarifyAnswer(scenario, slots, plan);
                    trace = "ClarificationPolicy:RequiredSlotsMissing";
                    break;
                case Route.Search:
                    properties = await SearchPropertiesAsync(slots, scenario);
                    answer = properties.Any() ? SearchResultAnswer(scenario, slots, properties) : NoResultAnswer(scenario, slots);
                    trace = properties.Any() ? BuildPropertyContext(properties, slots, scenario) : "SQLSearch:NoMatchedProperty";
                    break;
                case Route.PageAnalysis:
                    answer = PageAnalysisAnswer(page, normalized);
                    trace = "RuleBased:PageAnalysis";
                    break;
                case Route.Direct:
                    answer = DirectAnswer(original, normalized, scenario, slots, page);
                    trace = "RuleBased:DirectKnowledge";
                    break;
                default:
                    answer = await AiOrFallbackAsync(original, normalized, scenario, slots, plan, page, history);
                    trace = "AIOrFallback";
                    break;
            }

            answer = FinalGuard(answer, scenario, properties, page);
            string stage = route == Route.Clarify ? "CollectingNeed" : route == Route.Search ? "SearchResult" : "Answering";

            session.Scenario = scenario.Name;
            session.LastIntent = scenario.Intent;
            session.PageType = page.PageType;
            session.PageUrl = page.PageUrl;
            session.PageTitle = page.PageTitle;
            session.Stage = stage;
            session.CollectedDataJson = JsonSerializer.Serialize(CleanSlots(slots));
            session.UpdatedAt = DateTime.Now;

            _context.Set<AIChatMessage>().Add(new AIChatMessage { SessionID = session.SessionID, Role = "user", Content = original, Intent = scenario.Intent, CreatedAt = DateTime.Now });
            _context.Set<AIChatMessage>().Add(new AIChatMessage { SessionID = session.SessionID, Role = "assistant", Content = answer, Intent = scenario.Intent, ToolTrace = Trim(trace, 8000), CreatedAt = DateTime.Now });
            await _context.SaveChangesAsync();

            return BuildResponse(answer, scenario.Intent, scenario.Name, stage, session.SessionKey, slots, scenario.NeedHuman, Cards(properties, slots, scenario), RepliesForRoute(route, scenario.Name));
        }


        private static async Task ApplyThinkingDelayAsync(string normalized)
        {
            // Tạo cảm giác chatbot đang "suy nghĩ" thay vì trả lời quá vội.
            // Không đặt mặc định 10 giây vì dễ làm request bị xem là chậm; nếu cần demo quay lâu hơn,
            // chỉ cần đổi ThinkingDelayMilliseconds ở đầu class thành 5000 hoặc 10000.
            if (ThinkingDelayMilliseconds <= 0) return;
            if (ContainsAny(normalized, "hack", "gia so", "tron thue", "ca do")) return;
            await Task.Delay(ThinkingDelayMilliseconds);
        }

        private static ChatResponse BuildResponse(string message, string intent, string scenario, string stage, string sessionKey, Dictionary<string, string> slots, bool needHuman, List<object> cards, List<string> replies)
        {
            return new ChatResponse
            {
                Message = message,
                Intent = intent,
                Scenario = scenario,
                Stage = stage,
                SessionId = sessionKey,
                ShouldShowSuggestions = cards.Any(),
                SuggestedProperties = cards,
                SuggestedReplies = replies,
                NeedHumanSupport = needHuman,
                CollectedSlots = CleanSlots(slots)
            };
        }

        private async Task<AIChatSession> GetOrCreateSessionAsync(ChatRequest req, PageInfo page)
        {
            string key = string.IsNullOrWhiteSpace(req.SessionId) ? Guid.NewGuid().ToString("N") : req.SessionId.Trim();
            AIChatSession? session = await _context.Set<AIChatSession>().FirstOrDefaultAsync(x => x.SessionKey == key);
            if (session != null) return session;
            session = new AIChatSession { SessionKey = key, UserID = req.UserId > 0 ? req.UserId : null, Scenario = "General", Stage = "Start", PageType = page.PageType, PageUrl = page.PageUrl, PageTitle = page.PageTitle, LastIntent = "Start", CollectedDataJson = "{}", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
            _context.Set<AIChatSession>().Add(session);
            await _context.SaveChangesAsync();
            return session;
        }

        private async Task<List<AIChatMessage>> LoadRecentMessagesAsync(int sessionId)
        {
            return await _context.Set<AIChatMessage>().AsNoTracking().Where(x => x.SessionID == sessionId).OrderByDescending(x => x.CreatedAt).Take(14).OrderBy(x => x.CreatedAt).ToListAsync();
        }

        private async Task<Dictionary<string, string>> LoadSlotsAsync(int sessionId)
        {
            string? json = await _context.Set<AIChatSession>().AsNoTracking().Where(x => x.SessionID == sessionId).Select(x => x.CollectedDataJson).FirstOrDefaultAsync();
            if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
            try
            {
                Dictionary<string, string>? data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                return data == null ? new(StringComparer.OrdinalIgnoreCase) : new(data, StringComparer.OrdinalIgnoreCase);
            }
            catch { return new(StringComparer.OrdinalIgnoreCase); }
        }

        private static string ResolveShortChoice(string normalized, List<AIChatMessage> history)
        {
            if (!Regex.IsMatch(normalized ?? string.Empty, @"^[1-5]$")) return normalized;
            string last = Normalize(history.LastOrDefault(x => x.Role == "assistant")?.Content ?? string.Empty);
            return normalized switch
            {
                "1" when ContainsAny(last, "quy trinh ban lai") => "quy trinh ban lai bat dong san",
                "2" when ContainsAny(last, "yeu to anh huong den gia ban", "thanh khoan") => "cac yeu to anh huong den gia ban va thanh khoan khi ban lai",
                "3" when ContainsAny(last, "chi phi va thue", "thue khi ban lai") => "chi phi va thue khi ban lai bat dong san",
                _ => normalized
            };
        }

        private static void ResetSlotsForNewScenario(Dictionary<string, string> slots, Scenario sc, string lastScenario, string normalized)
        {
            if (slots.Count == 0) return;
            if (sc.Name == lastScenario || sc.Name == "PropertyDetail") return;
            if (Refinement(normalized) || Another(normalized)) return;

            bool hardSwitch = sc.Name is "Legal" or "Transaction" or "Loan" or "Posting" or "Project" or "Market" or "Care" or "Unsafe" or "OffTopic";
            bool buyRentSwitch = (sc.Name == "Buy" && lastScenario == "Rent") || (sc.Name == "Rent" && lastScenario == "Buy");
            if (!hardSwitch && !buyRentSwitch) return;

            string[] keys = { "deal_type", "property_type", "area_name", "ward_name", "budget_min", "budget_max", "rent_max", "area_min", "area_max", "purpose", "legal_requirement", "road_requirement", "amenities", "loan_need", "sort_price" };
            foreach (string key in keys) slots.Remove(key);
        }

        private static bool PostingNoExaggerationQuestion(string n)
        {
            return ContainsAny(n, "bao loi", "bao lai", "chac chan tang gia", "cam ket loi nhuan", "co nen ghi");
        }

        private static bool SellerPresentationQuestion(string n)
        {
            return ContainsAny(n, "ban nhanh", "khong muon bi ep gia", "bi ep gia", "trinh bay tin", "de co khach", "dang tin the nao");
        }

        private static bool HasPropertyNeedIntent(string n)
        {
            return Buy(n) || Rent(n) || Search(n) ||
                   (PropertyType(n) != null && (AreaName(n) != null || HasBudgetWords(n) || ContainsAny(n, "mua", "thue", "tim", "co nha", "co dat", "can mua", "muon mua")));
        }

        private static bool HasBudgetWords(string n)
        {
            return MoneyValues(n).Any() || MoneyRange(n).HasValue || ContainsAny(n, "duoi", "tren", "tam", "khoang", "ngan sach", "gia");
        }

        private static bool MortgageEligibilityQuestion(string n)
        {
            return ContainsAny(n,
                "mua dat co so do thi ngan hang co cho vay",
                "mua dat co so do ngan hang co cho vay",
                "co so do thi ngan hang co cho vay",
                "co so hong thi ngan hang co cho vay",
                "ngan hang co cho vay khong",
                "ngan hang co nhan the chap khong",
                "mua dat co so do",
                "mua nha co so hong");
        }

        private static bool BuildPermissionQuestion(string n)
        {
            return ContainsAny(n, "co duoc xay nha", "dat co duoc xay", "kiem tra dat co duoc xay", "duoc phep xay", "cap phep xay dung", "muc dich su dung dat", "dat o hay dat nong nghiep");
        }

        private static bool FeasibilityAdviceQuestion(string n)
        {
            return ContainsAny(n,
                       "co kha thi khong", "kha thi khong", "co on khong",
                       "nen chon", "chon dat mat tien hay nha xay san",
                       "vi tri dien tich hay phap ly", "so sanh", "nen mua",
                       "ngan sach gioi han", "giam yeu cau dien tich hay vi tri",
                       "giam dien tich hay vi tri", "dat mat tien gia cao", "mat tien gia cao",
                       "gia re co so duong nho", "duong nho co nen mua", "hem nho co nen mua")
                   && !ContainsAny(n, "co tin nao", "tim tin", "loc tin", "cho toi xem");
        }

        private static Scenario DetectScenario(string normalized, PageInfo page, AIChatSession session, Dictionary<string, string> slots)
        {
            if (Unsafe(normalized)) return new("Unsafe", "UnsafeIntent", false, false, false);
            if (OffTopic(normalized)) return new("OffTopic", "OffTopicIntent", false, false, false);

            // Ưu tiên đăng tin trước khi bắt lỗi/khiếu nại.
            // Ví dụ: "Có nên ghi bao lời..." là hỏi cách đăng tin an toàn, không phải "báo lỗi".
            if (PostingNoExaggerationQuestion(normalized) || SellerPresentationQuestion(normalized))
                return new("Posting", "SellerPostingIntent", false, false, false);

            if (Complaint(normalized)) return new("Care", "ComplaintReportIntent", false, false, true);
            if (Appointment(normalized)) return new("Care", "AppointmentGuideIntent", false, false, true);
            if (Support(normalized)) return new("Care", "CustomerSupportIntent", false, false, true);

            // Khi đang ở trang chi tiết, ưu tiên câu hỏi về tin hiện tại trước nhóm vay/market.
            // Ví dụ: "Giá này có hợp lý không?" không được nhảy sang tư vấn vay vốn.
            if (page.IsPropertyDetail && CurrentProperty(normalized))
                return new("PropertyDetail", "PropertyDetailIntent", false, false, false);

            // Các câu vay vốn/pháp lý độc lập phải ưu tiên trước MultiIntent, tránh câu
            // "Mua đất có sổ đỏ thì ngân hàng có cho vay không?" bị tách sai thành nhiều ý.
            if (MortgageEligibilityQuestion(normalized)) return new("Loan", "LoanAdviceIntent", false, false, false);
            if (BuildPermissionQuestion(normalized)) return new("Legal", "BuildPermissionLegalIntent", false, false, false);

            // Câu hỏi nhiều ý định: vừa tìm mua + pháp lý + vay vốn/dự án/giao dịch.
            // Không ép thành một intent đơn làm mất ý người dùng.
            if (IsMultiIntentQuestion(normalized))
                return new("MultiIntent", "MultiIntentAdvice", false, false, false);

            if (StandaloneLoan(normalized)) return new("Loan", "LoanAdviceIntent", false, false, false);
            if (StandaloneProject(normalized)) return new("Project", "ProjectConsultIntent", false, false, false);
            if (StandaloneTransaction(normalized)) return new("Transaction", "TransactionProcedureIntent", false, false, false);
            if (StandalonePosting(normalized)) return new("Posting", "SellerPostingIntent", false, false, false);
            if (StandaloneLegal(normalized)) return new("Legal", "LegalAdviceIntent", false, false, false);
            if (StandaloneMarket(normalized) || FeasibilityAdviceQuestion(normalized)) return new("Market", "MarketExperienceIntent", false, false, false);
            if (BuySafety(normalized)) return new("Buy", "BuySafetyAdviceIntent", true, false, false);

            bool buy = Buy(normalized);
            bool rent = Rent(normalized);
            bool explicitSearch = ExplicitListing(normalized);
            bool refine = Refinement(normalized) && (session.Scenario == "Buy" || session.Scenario == "Rent" || slots.ContainsKey("deal_type"));
            bool needContinuation = IsSearchNeedContinuation(normalized, session, slots);

            // Câu trả lời ngắn sau khi chatbot vừa hỏi thêm thông tin, ví dụ:
            // User: "tôi muốn mua 1 lô đất tại nha trang" -> bot hỏi ngân sách/mục đích/pháp lý
            // User: "giá dưới 20 tỷ, để ở và kinh doanh, có sổ đầy đủ"
            // Không được rơi về General/AI, mà phải giữ kịch bản Buy/Rent và lọc SQL nếu đã đủ slot.
            if (needContinuation)
                return SlotIs(slots, "deal_type", "Thuê") || session.Scenario == "Rent"
                    ? new("Rent", "SearchPropertyIntent", true, true, false)
                    : new("Buy", "SearchPropertyIntent", true, true, false);

            if (refine)
                return SlotIs(slots, "deal_type", "Thuê") || session.Scenario == "Rent"
                    ? new("Rent", "SearchRefinementIntent", true, true, false)
                    : new("Buy", "SearchRefinementIntent", true, true, false);

            if (rent) return new("Rent", explicitSearch ? "RentPropertyIntent" : "RentAdviceIntent", true, explicitSearch, false);
            if (buy) return new("Buy", explicitSearch ? "BuyPropertyIntent" : "BuyAdviceIntent", true, explicitSearch, false);

            if (Search(normalized) || Another(normalized))
                return SlotIs(slots, "deal_type", "Thuê")
                    ? new("Rent", "SearchPropertyIntent", true, true, false)
                    : new("Buy", "SearchPropertyIntent", true, true, false);

            if (Project(normalized) || page.IsProjectDetail) return new("Project", "ProjectConsultIntent", false, false, false);
            if (Legal(normalized)) return new("Legal", "LegalAdviceIntent", false, false, false);
            if (Transaction(normalized)) return new("Transaction", "TransactionProcedureIntent", false, false, false);
            if (Loan(normalized)) return new("Loan", "LoanAdviceIntent", false, false, false);
            if (Posting(normalized)) return new("Posting", "SellerPostingIntent", false, false, false);
            if (Market(normalized)) return new("Market", "MarketExperienceIntent", false, false, false);

            return new("General", "UnknownIntent", false, false, false);
        }

        private static Route DecideRoute(Scenario sc, Dictionary<string, string> slots, SlotPlan plan, string normalized, PageInfo page)
        {
            if (sc.Name == "Unsafe") return Route.Refuse;
            if (sc.Name == "OffTopic") return Route.OffTopic;
            if (sc.Name == "Care") return Route.Direct;
            if (sc.Name == "PropertyDetail") return Route.PageAnalysis;
            if (sc.Intent == "BuySafetyAdviceIntent") return Route.Direct;
            if (sc.Name is "Legal" or "Transaction" or "Loan" or "Posting" or "Project" or "Market" or "MultiIntent") return Route.Direct;
            if (sc.Name is "Buy" or "Rent")
            {
                if (!HasSearchCriteria(slots, sc.Name)) return Route.Clarify;
                return sc.ShouldSearch ? Route.Search : Route.Clarify;
            }
            // Với đồ án/demo, không nên gọi Gemini cho câu General mơ hồ vì dễ tốn quota 429.
            // Những nhóm quan trọng đã có rule-based/direct answer ở trên; General thì trả hướng dẫn ngắn.
            return Route.Direct;
        }

        private static void ExtractSlots(string normalized, string original, Dictionary<string, string> slots, Scenario sc, PageInfo page, List<AIChatMessage> history)
        {
            if (sc.Name == "Buy" || sc.Intent.Contains("Buy")) slots["deal_type"] = "Mua";
            if (sc.Name == "Rent" || sc.Intent.Contains("Rent")) slots["deal_type"] = "Thuê";

            ApplyShortAnswer(normalized, slots, history);

            // CỰC KỲ QUAN TRỌNG:
            // Câu follow-up như "giá dưới 20 tỷ, để ở và kinh doanh, có sổ đầy đủ"
            // là bổ sung mục đích/pháp lý/ngân sách, KHÔNG phải đổi từ "Đất" sang "Mặt bằng".
            // Chỉ ghi đè property_type khi người dùng nói rõ loại hình mới: đất/nhà/căn hộ/mặt bằng...
            string? type = PropertyType(normalized);
            if (!string.IsNullOrWhiteSpace(type) && ShouldOverridePropertyType(normalized, slots))
                slots["property_type"] = type;

            string? area = AreaName(normalized); if (!string.IsNullOrWhiteSpace(area)) slots["area_name"] = area;
            ExtractMoney(normalized, slots, sc);
            ExtractAreaSize(normalized, slots);
            string? purpose = Purpose(normalized); if (!string.IsNullOrWhiteSpace(purpose)) slots["purpose"] = purpose;
            string? legal = LegalNeed(normalized); if (!string.IsNullOrWhiteSpace(legal)) slots["legal_requirement"] = legal;
            string? road = RoadNeed(normalized); if (!string.IsNullOrWhiteSpace(road)) slots["road_requirement"] = road;
            string? amenity = Amenities(normalized); if (!string.IsNullOrWhiteSpace(amenity)) slots["amenities"] = amenity;
            if (ContainsAny(normalized, "vay", "ngan hang", "tra gop")) slots["loan_need"] = "Có quan tâm vay ngân hàng";
            Match propertyId = Regex.Match(page.PageUrl ?? "", @"/Property/Details/(?<id>\d+)", RegexOptions.IgnoreCase);
            if (propertyId.Success) slots["page_property_id"] = propertyId.Groups["id"].Value;
        }

        private static bool ShouldOverridePropertyType(string normalized, Dictionary<string, string> slots)
        {
            if (!slots.ContainsKey("property_type")) return true;

            // Chỉ đổi loại hình khi người dùng nêu trực tiếp loại BĐS mới.
            // Không xem các từ "kinh doanh", "để ở", "đầu tư" là loại hình.
            return ContainsAny(normalized,
                "lo dat", "dat nen", "dat tho cu", "mua dat", "tim dat",
                "nha rieng", "nha pho", "nha nguyen can", "mua nha", "tim nha",
                "can ho", "chung cu",
                "mat bang", "van phong", "cua hang", "shop", "shophouse", "showroom", "toa nha",
                "phong tro", "nha tro",
                "biet thu", "villa");
        }

        private static SlotPlan BuildSlotPlan(Scenario sc, Dictionary<string, string> slots, string normalized)
        {
            List<string> missing = new();
            if (sc.Name == "Buy")
            {
                if (!slots.ContainsKey("property_type")) missing.Add("property_type");
                if (!slots.ContainsKey("area_name")) missing.Add("area_name");
                if (!HasBudget(slots)) missing.Add("budget_any");
            }
            if (sc.Name == "Rent")
            {
                if (!slots.ContainsKey("property_type")) missing.Add("property_type");
                if (!slots.ContainsKey("area_name")) missing.Add("area_name");
                if (!slots.ContainsKey("rent_max") && !HasBudget(slots)) missing.Add("rent_or_budget");
            }
            if (sc.Intent == "BuySafetyAdviceIntent") missing.Clear();
            return new SlotPlan { Missing = missing };
        }

        private static bool HasSearchCriteria(Dictionary<string, string> slots, string scName)
        {
            bool deal = slots.ContainsKey("deal_type") || scName is "Buy" or "Rent";
            bool type = slots.ContainsKey("property_type");
            bool area = slots.ContainsKey("area_name");
            bool budget = HasBudget(slots);
            if (area && slots.TryGetValue("area_name", out string? areaName) && IsProvinceWideArea(Normalize(areaName)))
                return false;
            return deal && type && area && budget;
        }

        private async Task<List<Property>> SearchPropertiesAsync(Dictionary<string, string> slots, Scenario sc)
        {
            // Cổng chặn cuối: chỉ tìm khi đã đủ loại BĐS + khu vực + ngân sách.
            // Sửa phần nhận CSDL: mở rộng khu vực Khánh Hòa mới, gồm các khu vực Ninh Thuận cũ;
            // đồng thời khớp loại "mặt bằng kinh doanh" rộng hơn nhưng vẫn không bịa tin.
            if (!HasSearchCriteria(slots, sc.Name)) return new();

            IQueryable<Property> query = _context.Properties.AsNoTracking()
                .Include(p => p.Ward).ThenInclude(w => w.Area)
                .Include(p => p.PropertyType)
                .Include(p => p.PostServicePackage)
                .Where(p => p.IsDeleted != true &&
                    (p.Status == "Approved" || p.Status == "Active" || p.Status == "Published" ||
                     p.Status == "Đã duyệt" || p.Status == "Da duyet"));

            string deal = slots.TryGetValue("deal_type", out string? d) ? d : sc.Name == "Rent" ? "Thuê" : "Mua";

            if (deal.Equals("Thuê", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(p => p.PropertyType != null &&
                    (p.PropertyType.ParentID == 2 ||
                     p.PropertyType.TypeName.ToLower().Contains("thuê") || p.PropertyType.TypeName.ToLower().Contains("thue") ||
                     p.Title.ToLower().Contains("cho thuê") || p.Title.ToLower().Contains("cho thue") ||
                     p.Title.ToLower().Contains("thuê") || p.Title.ToLower().Contains("thue")));
            }
            else
            {
                query = query.Where(p => p.PropertyType != null &&
                    !(p.PropertyType.ParentID == 2 ||
                      p.PropertyType.TypeName.ToLower().Contains("thuê") || p.PropertyType.TypeName.ToLower().Contains("thue") ||
                      p.Title.ToLower().Contains("cho thuê") || p.Title.ToLower().Contains("cho thue") ||
                      p.Title.ToLower().Contains("thuê") || p.Title.ToLower().Contains("thue")));
            }

            decimal? max = DecimalSlot(slots, deal == "Thuê" && slots.ContainsKey("rent_max") ? "rent_max" : "budget_max");
            decimal? min = DecimalSlot(slots, "budget_min");
            decimal? amin = DecimalSlot(slots, "area_min");
            decimal? amax = DecimalSlot(slots, "area_max");

            if (min.HasValue) query = query.Where(p => p.Price.HasValue && p.Price.Value >= min.Value);
            if (max.HasValue) query = query.Where(p => p.Price.HasValue && p.Price.Value <= max.Value);
            if (amin.HasValue) query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value >= amin.Value);
            if (amax.HasValue) query = query.Where(p => p.AreaSize.HasValue && p.AreaSize.Value <= amax.Value);

            DateTime now = DateTime.Now;
            List<Property> list = await query
                .OrderByDescending(p => p.VipExpiryDate.HasValue && p.VipExpiryDate.Value > now)
                .ThenBy(p => p.PostServicePackage != null ? p.PostServicePackage.PriorityLevel : 999)
                .ThenByDescending(p => p.UpdatedAt ?? p.CreatedAt)
                .Take(1500)
                .ToListAsync();

            string typeNeed = Normalize(slots.GetValueOrDefault("property_type", ""));
            string areaNeed = Normalize(slots.GetValueOrDefault("area_name", ""));
            bool provinceWide = IsProvinceWideArea(areaNeed);

            IEnumerable<Property> strict = list;
            if (!string.IsNullOrWhiteSpace(areaNeed) && !provinceWide) strict = strict.Where(p => MatchArea(p, areaNeed));
            if (!string.IsNullOrWhiteSpace(typeNeed)) strict = strict.Where(p => MatchType(p, typeNeed));

            List<Property> result = strict
                .OrderByDescending(p => Score(p, slots, typeNeed, areaNeed, now))
                .GroupBy(p => p.PropertyID)
                .Select(g => g.First())
                .Take(MaxCards)
                .ToList();

            if (result.Any()) return result;

            // Fallback an toàn cho "mặt bằng kinh doanh": trong CSDL thực tế có thể lưu dưới dạng
            // Nhà phố, shophouse, văn phòng, cửa hàng, đất mặt tiền... Nếu không có TypeName đúng "Mặt bằng",
            // thử nới loại hình nhưng vẫn giữ khu vực + ngân sách + trạng thái tin thật.
            if (typeNeed.Contains("mat bang") || typeNeed.Contains("kinh doanh") || typeNeed.Contains("van phong") || typeNeed.Contains("shop"))
            {
                IEnumerable<Property> business = list;
                if (!string.IsNullOrWhiteSpace(areaNeed) && !provinceWide) business = business.Where(p => MatchArea(p, areaNeed));
                business = business.Where(IsBusinessSuitableProperty);

                result = business
                    .OrderByDescending(p => Score(p, slots, typeNeed, areaNeed, now))
                    .GroupBy(p => p.PropertyID)
                    .Select(g => g.First())
                    .Take(MaxCards)
                    .ToList();

                if (result.Any()) return result;
            }

            return new();
        }

        private static int Score(Property p, Dictionary<string, string> slots, string typeNeed, string areaNeed, DateTime now)
        {
            int s = 0;
            if (MatchType(p, typeNeed)) s += 35;
            if (MatchArea(p, areaNeed)) s += 35;
            decimal? max = DecimalSlot(slots, slots.ContainsKey("rent_max") ? "rent_max" : "budget_max");
            if (max.HasValue && p.Price.HasValue && p.Price.Value <= max.Value) s += 25;
            if (!string.IsNullOrWhiteSpace(p.MainImage)) s += 4;
            if (!string.IsNullOrWhiteSpace(p.Description) && p.Description.Length > 80) s += 4;
            if (p.VipExpiryDate.HasValue && p.VipExpiryDate.Value > now) s += 5;
            return s;
        }

        private static bool MatchType(Property p, string typeNeed)
        {
            if (string.IsNullOrWhiteSpace(typeNeed)) return true;

            string title = Normalize(p.Title ?? string.Empty);
            string typeName = Normalize(p.PropertyType?.TypeName ?? string.Empty);
            string description = Normalize(p.Description ?? string.Empty);
            string address = Normalize(p.AddressDetail ?? string.Empty);
            string text = $"{title} {typeName} {description} {address}";

            // CSDL đồ án có thể đặt tên loại khác nhau: "Mặt bằng", "BĐS kinh doanh",
            // "Nhà phố", "Shophouse", "Văn phòng", "Cửa hàng". Khớp rộng để nhận dữ liệu thật,
            // nhưng vẫn tránh lấy đất thường khi người dùng cần mặt bằng kinh doanh.
            if (typeNeed.Contains("mat bang") || typeNeed.Contains("kinh doanh") || typeNeed.Contains("van phong") || typeNeed.Contains("shop"))
                return IsBusinessSuitableProperty(p);

            if (typeNeed.Contains("can ho") || typeNeed.Contains("chung cu"))
                return ContainsAny(text, "can ho", "chung cu", "apartment");

            if (typeNeed.Contains("phong tro") || typeNeed.Contains("nha tro"))
                return ContainsAny(text, "phong tro", "nha tro", "can ho mini");

            if (typeNeed.Contains("dat"))
                return ContainsAny(text, "dat", "dat nen", "lo dat", "dat tho cu", "dat nong nghiep", "dat vuon")
                    && !ContainsAny(text, "nha pho", "nha rieng", "can ho", "chung cu", "mat bang");

            if (typeNeed.Contains("nha"))
                return ContainsAny(text, "nha", "nha pho", "nha rieng", "nha nguyen can", "biet thu", "villa", "shophouse")
                    && !ContainsAny(text, "dat nen", "lo dat", "dat tho cu", "can ho", "chung cu");

            return ContainsCompact(text, typeNeed);
        }

        private static bool IsBusinessSuitableProperty(Property p)
        {
            string text = Normalize($"{p.Title} {p.PropertyType?.TypeName} {p.Description} {p.AddressDetail}");

            bool directBusiness = ContainsAny(text,
                "mat bang", "mat bang kinh doanh", "kinh doanh", "van phong", "cua hang", "shop",
                "shophouse", "nha pho thuong mai", "nha mat tien", "mat tien", "toa nha", "mua ban",
                "mo quan", "spa", "cafe", "ca phe", "showroom", "khach san", "nha nghi");

            if (directBusiness) return true;

            // Nhà phố/nhà mặt tiền thường có thể phù hợp kinh doanh.
            bool houseBusinessCandidate = ContainsAny(text, "nha pho", "nha rieng", "biet thu", "villa") &&
                                          ContainsAny(text, "duong lon", "mat tien", "trung tam", "kinh doanh");

            return houseBusinessCandidate;
        }

        private static bool MatchArea(Property p, string areaNeed)
        {
            if (string.IsNullOrWhiteSpace(areaNeed)) return true;

            string title = Normalize(p.Title ?? string.Empty);
            string address = Normalize(p.AddressDetail ?? string.Empty);
            string ward = Normalize(p.Ward?.WardName ?? string.Empty);
            string area = Normalize(p.Ward?.Area?.AreaName ?? string.Empty);
            string text = $"{title} {address} {ward} {area}";

            List<string> aliases = AreaAliases(areaNeed);

            // Ưu tiên kiểm tra AreaName/WardName trước. Đây là dữ liệu địa giới chuẩn trong CSDL.
            foreach (string alias in aliases)
            {
                if (AreaPhraseEquals(area, alias) || AreaPhraseEquals(ward, alias)) return true;
            }

            // Sau đó mới kiểm tra tiêu đề/địa chỉ, nhưng vẫn phải khớp theo cụm địa danh thật, không khớp mơ hồ.
            foreach (string alias in aliases)
            {
                if (ContainsAreaPhrase(text, alias)) return true;
            }

            return false;
        }

        private static bool AreaPhraseEquals(string text, string alias)
        {
            text = Normalize(text);
            alias = Normalize(alias);
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(alias)) return false;
            return text == alias || ContainsAreaPhrase(text, alias);
        }

        private static bool ContainsAreaPhrase(string text, string alias)
        {
            string n = Normalize(text);
            string a = Normalize(alias);
            if (a.Length < 4) return Regex.IsMatch(n, $@"(^|\s){Regex.Escape(a)}($|\s)");

            // Khớp theo cụm từ có ranh giới khoảng trắng để tránh kiểu alias ngắn/một phần chữ kéo sai khu vực.
            return Regex.IsMatch(n, $@"(^|\s){Regex.Escape(a)}($|\s|,|\.|-)");
        }

        private static string ClarifyAnswer(Scenario sc, Dictionary<string, string> slots, SlotPlan plan)
        {
            if (sc.Name == "Buy")
            {
                List<string> q = new();
                if (!slots.ContainsKey("property_type")) q.Add("Anh/Chị muốn mua loại nào: đất, nhà, căn hộ hay mặt bằng?");
                if (!slots.ContainsKey("area_name")) q.Add("Khu vực mong muốn ở đâu: Nha Trang, Diên Khánh, Cam Lâm, Ninh Hòa, Cam Ranh hay khu khác?");
                if (!HasBudget(slots)) q.Add("Ngân sách tối đa khoảng bao nhiêu? Ví dụ: dưới 1 tỷ, khoảng 1,5 tỷ, từ 2–3 tỷ.");
                if (!slots.ContainsKey("purpose")) q.Add("Mục đích mua là để ở, đầu tư, kinh doanh hay giữ tài sản?");
                if (!slots.ContainsKey("legal_requirement")) q.Add("Anh/Chị có bắt buộc sổ riêng/pháp lý rõ không?");
                return $"""
                {NeedSummary(slots, sc)}

                Em **chưa đề xuất tin ngay** vì thiếu thông tin lõi thì kết quả dễ sai. Để lọc chính xác hơn, Anh/Chị cho em biết:

                {string.Join("\n", q.Take(4).Select((x, i) => $"{i + 1}. {x}"))}

                Khi đủ thông tin, em mới lọc từ dữ liệu nội bộ để tránh gợi ý bậy.
                """;
            }
            if (sc.Name == "Rent")
            {
                List<string> q = new();
                if (!slots.ContainsKey("property_type")) q.Add("Anh/Chị muốn thuê loại nào: nhà nguyên căn, căn hộ, phòng trọ, mặt bằng hay văn phòng?");
                if (!slots.ContainsKey("area_name")) q.Add("Khu vực muốn thuê ở đâu?");
                if (!slots.ContainsKey("rent_max") && !HasBudget(slots)) q.Add("Ngân sách thuê mỗi tháng khoảng bao nhiêu?");
                if (!slots.ContainsKey("purpose")) q.Add("Thuê để ở, làm văn phòng hay kinh doanh?");
                return $"""
                {NeedSummary(slots, sc)}

                Em chưa đề xuất tin thuê ngay vì còn thiếu thông tin quan trọng. Anh/Chị cho em biết thêm:

                {string.Join("\n", q.Take(4).Select((x, i) => $"{i + 1}. {x}"))}
                """;
            }
            return "Anh/Chị cho em thêm khu vực, ngân sách và loại bất động sản để em tư vấn chính xác hơn nhé.";
        }

        private static string SearchResultAnswer(Scenario sc, Dictionary<string, string> slots, List<Property> props)
        {
            return $"""
            {NeedSummary(slots, sc)}

            Em tìm được **{Math.Min(MaxCards, props.Count)} tin** khớp tốt nhất trong dữ liệu nội bộ. Em chỉ hiển thị khi đã có đủ tiêu chí lõi để tránh đề xuất sai.

            **Lý do ưu tiên tin đầu tiên:** {Reason(props.First(), slots, sc)}

            Lưu ý: Em không tự cam kết pháp lý/quy hoạch. Trước khi đặt cọc, Anh/Chị vẫn cần kiểm tra sổ gốc, chủ sở hữu, quy hoạch, tranh chấp, thế chấp và xem thực tế.
            """;
        }

        private static string NoResultAnswer(Scenario sc, Dictionary<string, string> slots)
        {
            return $"""
            {NeedSummary(slots, sc)}

            Hiện tại em **chưa tìm thấy tin nào khớp hoàn toàn** với các tiêu chí này trong dữ liệu nội bộ.

            Em không muốn tự bịa hoặc lấy tin sai khu vực/sai loại hình để lấp chỗ trống. Anh/Chị có thể nới ngân sách, mở rộng khu vực, bỏ bớt tiêu chí phụ hoặc hỏi checklist pháp lý/giao dịch trước khi tìm tin.
            """;
        }

        private static string DirectAnswer(string original, string normalized, Scenario sc, Dictionary<string, string> slots, PageInfo page)
        {
            return sc.Name switch
            {
                "Buy" when sc.Intent == "BuySafetyAdviceIntent" => BuySafetyAnswer(),
                "Legal" => LegalAnswer(normalized),
                "Transaction" => TransactionAnswer(normalized),
                "Loan" => LoanAnswer(slots, normalized),
                "Posting" => PostingAnswer(normalized),
                "Project" => ProjectAnswer(normalized),
                "Care" => CareAnswer(normalized, slots),
                "Market" => MarketAnswer(normalized),
                "MultiIntent" => MultiIntentAnswer(normalized, slots),
                _ => GeneralAnswer()
            };
        }

        private static string MultiIntentAnswer(string n, Dictionary<string, string> slots)
        {
            StringBuilder sb = new();
            sb.AppendLine("Em thấy câu hỏi của Anh/Chị có nhiều ý, em tách ra để trả lời đúng từng phần:");
            sb.AppendLine();

            int index = 1;
            if (HasPropertyNeedIntent(n))
            {
                sb.AppendLine($"**{index++}. Về nhu cầu bất động sản**");
                string type = PropertyType(n) ?? slots.GetValueOrDefault("property_type", "bất động sản");
                string area = AreaName(n) ?? slots.GetValueOrDefault("area_name", "khu vực Anh/Chị quan tâm");
                string budget = MoneyValues(n).Any() ? Price(MoneyValues(n).Max()) : slots.TryGetValue("budget_max", out string? b) && decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal bd) ? Price(bd) : "ngân sách đã nêu";
                sb.AppendLine($"- Em hiểu Anh/Chị đang quan tâm **{type.ToLower(Vi)}** tại **{area}** với mức khoảng **{budget}**. Nếu đủ loại BĐS + khu vực cụ thể + ngân sách, em mới lọc tin SQL; nếu khu vực còn rộng như toàn Khánh Hòa thì nên chọn thêm Nha Trang, Diên Khánh, Cam Lâm, Ninh Hòa, Cam Ranh... để tránh gợi ý sai.");
                sb.AppendLine();
            }

            if (Legal(n) || BuildPermissionQuestion(n))
            {
                sb.AppendLine($"**{index++}. Về pháp lý**");
                if (BuildPermissionQuestion(n))
                    sb.AppendLine("- Muốn biết đất có được xây nhà không, cần xem trên sổ là đất ở hay loại đất khác, kiểm tra quy hoạch/lộ giới, điều kiện cấp phép xây dựng và hỏi cơ quan có thẩm quyền trước khi đặt cọc.");
                else
                    sb.AppendLine("- Cần kiểm tra sổ gốc, chủ sở hữu, quy hoạch/lộ giới, tranh chấp, thế chấp, mục đích sử dụng đất, diện tích thực tế và điều kiện sang tên.");
                sb.AppendLine();
            }

            if (Loan(n))
            {
                sb.AppendLine($"**{index++}. Về vay ngân hàng**");
                sb.AppendLine("- Có sổ đỏ/sổ hồng là điểm thuận lợi, nhưng ngân hàng vẫn thẩm định pháp lý, quy hoạch, giá trị tài sản, thu nhập, nợ hiện có và lịch sử tín dụng. Muốn tính áp lực vay, Anh/Chị gửi thêm giá tài sản, vốn tự có, thu nhập tháng và thời hạn vay.");
                sb.AppendLine();
            }

            if (Transaction(n))
            {
                sb.AppendLine($"**{index++}. Về giao dịch**");
                sb.AppendLine("- Đi theo luồng: xem thực tế → kiểm tra pháp lý → đặt cọc có điều kiện hoàn cọc rõ → công chứng → kê khai thuế phí → sang tên → bàn giao.");
                sb.AppendLine();
            }

            if (Posting(n))
            {
                sb.AppendLine($"**{index++}. Về đăng tin/bán tài sản**");
                sb.AppendLine("- Nên viết tiêu đề rõ, mô tả trung thực, ảnh thật, giá minh bạch; không ghi quá đà như ‘bao lời’ hoặc ‘chắc chắn tăng giá’.");
                sb.AppendLine();
            }

            if (Project(n))
            {
                sb.AppendLine($"**{index++}. Về dự án**");
                sb.AppendLine("- Cần kiểm tra chủ đầu tư, pháp lý dự án, điều kiện mở bán, tiến độ, bảo lãnh ngân hàng nếu áp dụng, hợp đồng mẫu và điều kiện cấp sổ.");
                sb.AppendLine();
            }

            sb.AppendLine("Thông tin này là tư vấn ban đầu, không thay thế ngân hàng, luật sư, công chứng viên hoặc cơ quan nhà nước.");
            return sb.ToString();
        }

        private static string BuySafetyAnswer() => """
        Em hiểu Anh/Chị muốn **mua đất nhưng chưa biết mua như thế nào cho an toàn**. Câu này là tư vấn an toàn, nên em **không đề xuất tin ngay**.

        **Checklist mua đất an toàn lần đầu**
        1. Xác định nhu cầu: mua để ở, xây nhà, đầu tư, kinh doanh hay giữ tài sản; ngân sách và có vay hay không.
        2. Xem sổ đỏ/sổ hồng bản gốc, không chỉ xem ảnh chụp; đối chiếu người bán với tên trên sổ.
        3. Kiểm tra quy hoạch/lộ giới, tranh chấp, thế chấp, ngăn chặn giao dịch, mục đích sử dụng đất.
        4. Xem thực tế đường vào, điện nước, thoát nước, ranh giới, diện tích thực tế và khu dân cư.
        5. Không đặt cọc vội. Hợp đồng cọc phải ghi rõ giá, thời hạn công chứng, phạt cọc/hoàn cọc và trách nhiệm nếu pháp lý không đúng.
        6. Nếu có vay ngân hàng, nên hỏi thẩm định sơ bộ trước khi cọc lớn.

        Đây là tư vấn tham khảo, không thay thế luật sư, công chứng viên hoặc cơ quan nhà nước.

        Nếu Anh/Chị muốn em lọc tin phù hợp, hãy cho em biết: **khu vực**, **ngân sách**, **diện tích mong muốn**, và **mua để ở hay đầu tư**.
        """;

        private static string PageAnalysisAnswer(PageInfo page, string normalized)
        {
            if (!page.HasUsefulContext) return "Em chưa đọc được đầy đủ thông tin tin đang xem nên không muốn phân tích bừa. Anh/Chị gửi giúp em tiêu đề, giá, diện tích, vị trí và pháp lý hiển thị; em sẽ đánh giá rủi ro và bước nên làm tiếp.";

            string title = PageField(page, "Tiêu đề") ?? CleanTitle(page.PageTitle, "tin đang xem");
            string price = PageField(page, "Giá") ?? "chưa thấy giá rõ trong ngữ cảnh";
            string area = PageField(page, "Diện tích") ?? "chưa thấy diện tích rõ trong ngữ cảnh";
            string unitPrice = PageField(page, "Đơn giá") ?? "chưa thấy đơn giá rõ";
            string location = PageField(page, "Vị trí") ?? "chưa thấy vị trí rõ trong ngữ cảnh";
            string region = PageField(page, "Khu vực") ?? "chưa thấy khu vực rõ";
            string ward = PageField(page, "Phường/Xã") ?? "chưa thấy xã/phường rõ";
            string type = PageField(page, "Loại bất động sản") ?? PageField(page, "Loại BĐS") ?? "chưa thấy loại hình rõ";
            string legal = PageField(page, "Pháp lý") ?? "chưa thấy pháp lý rõ";
            string utilities = PageField(page, "Tiện ích") ?? "chưa thấy tiện ích rõ";
            string bedrooms = PageField(page, "Phòng ngủ") ?? "chưa thấy";
            string bathrooms = PageField(page, "Phòng tắm") ?? "chưa thấy";
            string status = PageField(page, "Tình trạng tin") ?? "chưa thấy";
            string postedDate = PageField(page, "Ngày đăng") ?? "chưa thấy";
            string owner = PageField(page, "Người đăng") ?? PageField(page, "Chủ tin") ?? PageField(page, "Người bán") ?? PageField(page, "Môi giới");

            if (ContainsAny(normalized, "o khu vuc nao", "khu vuc nao", "o dau", "dia chi", "vi tri", "dat nay o"))
                return $"Tin này ở **{region}**, xã/phường **{ward}**. Vị trí trang cung cấp: **{location}**. Anh/Chị nên đối chiếu địa chỉ này khi đi xem thực tế và kiểm tra trên giấy tờ/sổ gốc để tránh sai vị trí.";

            if (ContainsAny(normalized, "ai la chu", "chu cua tin", "nguoi dang", "ho uy tin", "uy tin khong", "chu tin"))
            {
                string ownerLine = string.IsNullOrWhiteSpace(owner) ? "Hiện ngữ cảnh trang chưa cung cấp rõ tên/người đăng tin nên em không thể kết luận họ là ai." : $"Ngữ cảnh trang cho thấy người đăng/chủ tin là: **{owner}**.";
                return $"""
                {ownerLine}

                Về mức độ uy tín, em **không nên khẳng định chắc chắn** chỉ dựa trên tin đăng. Anh/Chị nên kiểm tra thêm:
                1. Tài khoản có xác thực không, số điện thoại/email có rõ không.
                2. Người liên hệ có đúng chủ sở hữu hoặc được ủy quyền hợp lệ không.
                3. Có cho xem sổ gốc, CCCD và giấy tờ liên quan không.
                4. Có lịch sử đăng tin bất thường, giá quá rẻ, yêu cầu chuyển cọc sớm không.
                5. Khi đi xem nên hẹn tại tài sản thật hoặc văn phòng rõ ràng, không chuyển tiền trước khi xác minh.
                """;
            }

            if (ContainsAny(normalized, "gia re", "re hon khu vuc", "duong nho", "hem nho", "co so", "co nen mua khong", "co nen mua"))
                return $"""
                Với tin này, em **chưa thể kết luận nên mua hay không** chỉ dựa trên mô tả. Nếu tin có **giá rẻ + có sổ + đường nhỏ**, Anh/Chị nên xem đây là trường hợp cần kiểm tra kỹ hơn, không nên cọc vội.

                **Điểm có thể tốt**
                - Có pháp lý hiển thị: **{legal}**.
                - Giá/diện tích trang ghi: **{price}**, **{area}**.
                - Khu vực: **{region}**, xã/phường **{ward}**.

                **Rủi ro cần kiểm tra**
                1. Đường vào có phải lối đi hợp pháp không, xe cứu hỏa/ô tô có vào được không, có tranh chấp lối đi không.
                2. Sổ ghi loại đất gì: đất ở hay loại đất khác.
                3. Có vướng quy hoạch/lộ giới/hành lang bảo vệ không.
                4. Giá rẻ vì cần bán nhanh hay vì lỗi pháp lý, vị trí, đường vào, quy hoạch.
                5. Nếu mua để ở lâu dài, đường nhỏ có ảnh hưởng sinh hoạt, xây dựng, thoát nước và thanh khoản sau này không.

                **Kết luận sơ bộ:** chỉ nên đi xem thực tế và kiểm tra sổ/quy hoạch trước. Em không khuyên đặt cọc ngay khi chưa xác minh đủ.
                """;

            if (ContainsAny(normalized, "bao cao", "vi pham", "tin sai", "lua dao", "tin gia"))
                return $"""
                Nếu Anh/Chị nghi tin này vi phạm hoặc có dấu hiệu sai sự thật, hãy dùng chức năng **Báo cáo vi phạm** trên trang chi tiết tin.

                Nên ghi rõ:
                1. Lý do báo cáo: sai giá, sai vị trí, ảnh không đúng, pháp lý không rõ, nghi lừa đảo, đã bán nhưng vẫn đăng.
                2. Bằng chứng nếu có: ảnh chụp màn hình, nội dung chat, số điện thoại, thông tin chuyển khoản.
                3. Mã/link tin: **{page.PageUrl}**.

                Không nên tiếp tục chuyển cọc hoặc cung cấp giấy tờ cá nhân nếu chưa xác minh được người đăng và pháp lý tài sản.
                """;

            if (ContainsAny(normalized, "ban lai", "thanh khoan", "sau nay ban", "de ban lai"))
                return $"""
                Với tin này, nếu xét khả năng **bán lại/thanh khoản**, em nhìn theo dữ liệu trang cung cấp:

                - Loại BĐS: **{type}**.
                - Giá: **{price}**, diện tích **{area}**, đơn giá **{unitPrice}**.
                - Khu vực: **{region}**, xã/phường **{ward}**.
                - Pháp lý hiển thị: **{legal}**.
                - Tiện ích/điểm mô tả: **{utilities}**.

                Khả năng bán lại thường tốt hơn khi: pháp lý rõ, giá không quá cao so với khu vực, đường vào thuận tiện, khu dân cư/tiện ích có nhu cầu thật, và loại hình phù hợp nhu cầu địa phương. Với khu vực ngoài trung tâm như **{region}**, Anh/Chị nên kiểm tra thêm thanh khoản thực tế: xung quanh có giao dịch không, tin tương tự đăng bao lâu, đường vào và quy hoạch có ảnh hưởng không.

                Em không cam kết chắc chắn bán lại dễ. Trước khi mua để bán lại, nên so sánh thêm 3–5 tin cùng khu vực, cùng diện tích và cùng pháp lý.
                """;

            if (ContainsAny(normalized, "xay nha", "cat nha", "de o", "o lau dai", "mua de o"))
                return $"""
                Nếu mua tin này để **cất/xây nhà ở**, em đánh giá sơ bộ theo thông tin trang đang có:

                **Thông tin chính**
                - Loại BĐS: **{type}**.
                - Giá: **{price}**; diện tích: **{area}**.
                - Khu vực: **{region}**, xã/phường **{ward}**.
                - Pháp lý hiển thị: **{legal}**.
                - Tiện ích: **{utilities}**.

                **Có thể phù hợp để ở nếu:** pháp lý đúng là sổ riêng/sổ hồng riêng, đất có mục đích sử dụng phù hợp xây dựng nhà ở, đường vào và hạ tầng ổn, khu dân cư phù hợp sinh hoạt gia đình.

                **Cần kiểm tra trước khi quyết định:**
                1. Trên sổ ghi loại đất gì: đất ở hay loại đất khác.
                2. Có vướng quy hoạch/lộ giới/hành lang bảo vệ không.
                3. Có được cấp phép xây dựng hoặc cải tạo không.
                4. Đường vào, điện nước, thoát nước, an ninh và khoảng cách tới trường/chợ/bệnh viện.
                5. Diện tích thực tế có khớp sổ và ranh giới ngoài thực địa không.

                Em chưa khuyên cọc ngay. Anh/Chị nên đi xem thực tế và kiểm tra sổ gốc/quy hoạch trước.
                """;

            if (ContainsAny(normalized, "so do", "so hong", "phap ly", "kiem tra gi", "rui ro"))
                return $"""
                Với tin này, pháp lý trang hiển thị là: **{legal}**. Tuy nhiên, thông tin trên tin đăng chỉ là bước đầu.

                Anh/Chị cần kiểm tra trực tiếp:
                1. Sổ gốc: số thửa, tờ bản đồ, diện tích, mục đích sử dụng đất, thời hạn sử dụng.
                2. Chủ sở hữu/người ký có đúng tên trên sổ hoặc có ủy quyền hợp lệ không.
                3. Quy hoạch, lộ giới, tranh chấp, thế chấp, ngăn chặn giao dịch.
                4. Diện tích thực tế và ranh giới có khớp sổ không.
                5. Nếu là nhà/công trình: có hoàn công hoặc giấy tờ xây dựng liên quan không.

                Không nên đặt cọc chỉ dựa trên chữ “{legal}” trong tin đăng.
                """;

            if (ContainsAny(normalized, "gia", "hop ly", "thuong luong", "mac ca"))
                return $"""
                Tin này đang hiển thị giá **{price}**, diện tích **{area}**, đơn giá **{unitPrice}** tại **{region}**.

                Em chưa thể kết luận chắc chắn rẻ hay đắt nếu không có dữ liệu so sánh cùng khu vực/cùng loại hình/cùng pháp lý. Khi thương lượng, Anh/Chị nên dựa vào:
                1. Giá các tin tương tự tại {region}.
                2. Đường vào, diện tích thực tế, pháp lý, quy hoạch.
                3. Chi phí sửa chữa/cải tạo/xây dựng nếu có.
                4. Tin đăng lâu chưa bán hoặc thông tin còn thiếu là cơ sở thương lượng.
                """;

            return $"""
            Em đang đọc được thông tin chính của tin này như sau:

            - Tiêu đề: **{title}**
            - Loại BĐS: **{type}**
            - Giá: **{price}**; diện tích: **{area}**; đơn giá: **{unitPrice}**
            - Vị trí: **{location}**
            - Khu vực: **{region}**; phường/xã: **{ward}**
            - Pháp lý hiển thị: **{legal}**
            - Phòng ngủ/phòng tắm: **{bedrooms}/{bathrooms}**
            - Tình trạng/ngày đăng: **{status}**, **{postedDate}**

            Nhận xét sơ bộ: tin có đủ nhiều thông tin để Anh/Chị cân nhắc bước tiếp theo, nhưng chưa nên đặt cọc nếu chưa kiểm tra sổ gốc, quy hoạch, chủ sở hữu, tranh chấp/thế chấp và xem thực tế.
            """;
        }

        private static string LegalAnswer(string n)
        {
            if (ContainsAny(n, "so do va so hong", "so hong voi so do", "khac nhau")) return """
            **Sổ đỏ và sổ hồng khác nhau thế nào?**

            Đây là cách gọi phổ biến theo màu bìa qua từng thời kỳ. Khi mua bán, Anh/Chị không nên chỉ nhìn tên gọi mà cần kiểm tra nội dung trên giấy chứng nhận:
            1. Người đứng tên/chủ sở hữu là ai.
            2. Số thửa, tờ bản đồ, diện tích, mục đích sử dụng đất.
            3. Thời hạn sử dụng đất, tài sản gắn liền với đất.
            4. Ghi chú hạn chế quyền, thế chấp, quy hoạch, lộ giới nếu có.
            5. Diện tích thực tế có khớp với giấy chứng nhận không.

            Trước khi cọc nên xem bản gốc và xác minh tại văn phòng đăng ký đất đai/công chứng.
            """;

            if (ContainsAny(n, "quy hoach", "dinh quy hoach", "vuong quy hoach", "kiem tra quy hoach")) return """
            **Kiểm tra quy hoạch như thế nào cho chắc?**

            Anh/Chị nên kiểm tra từ nguồn chính thức thay vì chỉ nghe người bán nói:
            1. Tra cứu tại cơ quan quản lý đất đai/quy hoạch địa phương hoặc cổng thông tin quy hoạch nếu có.
            2. Đối chiếu số thửa, tờ bản đồ, vị trí thực tế trên sổ.
            3. Hỏi rõ có nằm trong lộ giới, đường dự phóng, hành lang bảo vệ, đất cây xanh/công cộng không.
            4. Kiểm tra có trong kế hoạch sử dụng đất hằng năm hoặc có thông báo thu hồi chưa.
            5. Ghi điều kiện hoàn cọc nếu phát hiện thông tin quy hoạch không đúng.

            Nếu mua để ở/xây nhà lâu dài, đất vướng quy hoạch là rủi ro lớn và không nên cọc khi chưa xác minh rõ.
            """;

            if (BuildPermissionQuestion(n)) return "Muốn biết đất có được xây nhà không, Anh/Chị cần kiểm tra trên sổ ghi mục đích sử dụng đất là đất ở hay loại đất khác, đối chiếu quy hoạch/lộ giới, hỏi điều kiện cấp phép xây dựng tại địa phương và kiểm tra có bị hạn chế xây dựng không. Nếu đất là đất nông nghiệp hoặc đất chưa chuyển mục đích thì không nên mặc định được xây nhà ở; cần xác minh trước khi đặt cọc.";

            if (ContainsAny(n, "the chap", "ngan hang")) return "Đất/nhà đang thế chấp vẫn có thể giao dịch trong một số trường hợp, nhưng phải giải chấp đúng quy trình. Anh/Chị cần kiểm tra ngân hàng giữ sổ, dư nợ, thời hạn giải chấp, ai chịu trách nhiệm giải chấp và chỉ công chứng/chuyển tiền lớn khi có cơ chế an toàn tại ngân hàng hoặc văn phòng công chứng.";
            if (ContainsAny(n, "giay tay")) return "Mua đất giấy tay có rủi ro cao: khó sang tên, khó chứng minh quyền sử dụng, dễ tranh chấp, khó vay ngân hàng và có thể không được xây dựng/hợp thức hóa. Không nên cọc nếu chưa được cơ quan có thẩm quyền hoặc luật sư kiểm tra điều kiện pháp lý.";
            if (ContainsAny(n, "anh chup so", "chi cho xem anh")) return "Chỉ xem ảnh chụp sổ thì chưa đủ để đặt cọc. Anh/Chị cần xem sổ gốc, đối chiếu người đứng tên, kiểm tra quy hoạch, tranh chấp, thế chấp/ngăn chặn giao dịch và ghi điều kiện hoàn cọc nếu pháp lý không đúng.";
            if (ContainsAny(n, "nha cu", "mua nha cu")) return "Mua nhà cũ ngoài kiểm tra đất/sổ như mua đất trống, còn phải kiểm tra thêm hiện trạng nhà, kết cấu, thấm dột, hệ thống điện nước, giấy phép xây dựng/hoàn công nếu có, tài sản gắn liền với đất có được ghi nhận trên sổ không và có tranh chấp sử dụng chung lối đi/tường/rào không.";
            if (ContainsAny(n, "dat nong nghiep", "xay nha duoc khong", "dat co duoc xay", "kiem tra dat co duoc xay", "co duoc xay nha")) return "Muốn biết đất có được xây nhà không, Anh/Chị cần kiểm tra: mục đích sử dụng đất trên sổ có phải đất ở không, có vướng quy hoạch/lộ giới/hành lang bảo vệ không, khu vực có được cấp phép xây dựng không, diện tích/lối đi có đủ điều kiện không. Đất nông nghiệp thường không được tự ý xây nhà nếu chưa chuyển mục đích và phù hợp quy hoạch.";
            if (ContainsAny(n, "dong so huu", "thua ke")) return "Tài sản đồng sở hữu hoặc thừa kế vẫn có thể giao dịch nếu đủ người có quyền đồng ý và hồ sơ hợp lệ. Cần kiểm tra tất cả đồng sở hữu/người thừa kế, giấy tờ hôn nhân, ủy quyền, văn bản khai nhận/phân chia di sản và điều kiện công chứng.";

            return """
            **Checklist kiểm tra sổ đỏ/sổ hồng khi mua nhà đất**

            1. Xem bản gốc, không chỉ xem ảnh chụp.
            2. Kiểm tra người đứng tên, đồng sở hữu, vợ/chồng, thừa kế hoặc ủy quyền.
            3. Kiểm tra số thửa, tờ bản đồ, diện tích, mục đích sử dụng đất, thời hạn sử dụng.
            4. Đối chiếu diện tích thực tế, ranh giới, đường vào với thông tin trên sổ.
            5. Kiểm tra quy hoạch/lộ giới, tranh chấp, thế chấp, ngăn chặn giao dịch.
            6. Nếu có nhà/công trình: kiểm tra hoàn công, giấy phép xây dựng hoặc tài sản gắn liền với đất.
            7. Trước khi cọc, ghi rõ điều kiện hoàn cọc nếu pháp lý không đúng như cam kết.

            Đây là tư vấn tham khảo, Anh/Chị nên xác minh tại văn phòng đăng ký đất đai, văn phòng công chứng hoặc cơ quan có thẩm quyền.
            """;
        }

        private static string TransactionAnswer(string n = "")
        {
            if (ContainsAny(n, "chi phi", "thue khi ban lai", "ban lai", "thue ban lai")) return """
            **Chi phí và thuế khi bán lại bất động sản cần lưu ý**

            Khi bán lại nhà đất, các khoản thường cần xem xét gồm:
            1. Thuế thu nhập cá nhân từ chuyển nhượng bất động sản theo quy định áp dụng tại thời điểm giao dịch.
            2. Lệ phí trước bạ thường do bên mua chịu nếu hai bên không thỏa thuận khác.
            3. Phí công chứng hợp đồng chuyển nhượng.
            4. Phí thẩm định/hồ sơ/sang tên nếu có.
            5. Chi phí môi giới, quảng cáo tin, đo vẽ, trích lục, sửa chữa hồ sơ nếu phát sinh.

            Ai chịu khoản nào cần ghi rõ trong đặt cọc/hợp đồng. Em không hướng dẫn né thuế; nên kê khai đúng và hỏi văn phòng công chứng/cơ quan thuế để có số tiền chính xác.
            """;

            if (ContainsAny(n, "dat coc", "hop dong dat coc", "dieu khoan", "ghi dieu khoan")) return """
            **Đặt cọc mua đất cần ghi điều khoản gì để an toàn?**

            Hợp đồng đặt cọc nên ghi rõ:
            1. Thông tin bên mua, bên bán, người được ủy quyền nếu có.
            2. Thông tin tài sản: số thửa, tờ bản đồ, diện tích, địa chỉ, loại đất, tình trạng sổ.
            3. Giá mua bán, số tiền cọc, phương thức thanh toán và các mốc thanh toán.
            4. Thời hạn công chứng/chuyển nhượng.
            5. Ai chịu thuế, phí công chứng, lệ phí trước bạ và chi phí phát sinh.
            6. Điều kiện hoàn cọc nếu phát hiện quy hoạch, tranh chấp, thế chấp, sai diện tích, sai chủ sở hữu hoặc không đủ điều kiện sang tên.
            7. Mức phạt cọc nếu một bên tự ý hủy giao dịch.
            8. Thời điểm bàn giao đất/nhà và giấy tờ.

            Không nên cọc lớn nếu chưa xem sổ gốc, chưa kiểm tra quy hoạch và chưa xác minh người bán có quyền ký.
            """;

            if (ContainsAny(n, "cong chung", "giay to cong chung", "ben mua can chuan bi", "ben ban can chuan bi")) return """
            **Công chứng mua bán nhà đất cần chuẩn bị gì?**

            Bên mua thường cần: CCCD/hộ chiếu, giấy đăng ký kết hôn hoặc xác nhận độc thân, thông tin cư trú và tiền/thỏa thuận thanh toán.

            Bên bán thường cần: CCCD/hộ chiếu, giấy đăng ký kết hôn hoặc xác nhận độc thân, sổ đỏ/sổ hồng bản gốc, giấy tờ liên quan đến tài sản, giấy ủy quyền hợp lệ nếu không trực tiếp ký.

            Trước khi công chứng nên hỏi văn phòng công chứng danh sách hồ sơ cụ thể vì từng trường hợp đồng sở hữu, thừa kế, ủy quyền, tài sản chung vợ chồng sẽ khác nhau.
            """;

            if (ContainsAny(n, "sau khi cong chung", "lam gi tiep", "nhan so", "bao lau")) return """
            **Sau khi công chứng xong thì làm gì tiếp?**

            Thông thường các bước tiếp theo gồm:
            1. Kê khai thuế thu nhập cá nhân, lệ phí trước bạ và các khoản phí liên quan.
            2. Nộp hồ sơ sang tên tại cơ quan tiếp nhận hồ sơ đất đai.
            3. Theo dõi thông báo thuế/phí và hoàn tất nghĩa vụ tài chính.
            4. Nhận giấy chứng nhận đã sang tên.
            5. Bàn giao tài sản, chìa khóa, giấy tờ và xác nhận thanh toán còn lại nếu có.

            Thời gian nhận sổ phụ thuộc địa phương, hồ sơ và tình trạng pháp lý tài sản.
            """;

            if (ContainsAny(n, "dieu khoan gi", "ghi dieu khoan", "dat coc mua dat can ghi", "dat coc can ghi", "hop dong coc can ghi")) return """
            **Đặt cọc mua đất cần ghi điều khoản gì để an toàn?**

            Hợp đồng đặt cọc nên ghi rõ:
            1. Thông tin hai bên và căn cứ người bán có quyền bán: chủ sở hữu, đồng sở hữu, ủy quyền nếu có.
            2. Thông tin tài sản: số thửa, tờ bản đồ, diện tích, địa chỉ, mục đích sử dụng đất, tài sản gắn liền với đất.
            3. Giá chuyển nhượng, số tiền cọc, phương thức thanh toán, từng mốc thanh toán.
            4. Thời hạn ký công chứng và điều kiện gia hạn nếu có.
            5. Điều kiện hoàn cọc nếu phát hiện quy hoạch, tranh chấp, thế chấp, sai diện tích, sai chủ sở hữu hoặc không đủ điều kiện sang tên.
            6. Mức phạt cọc nếu bên mua/bên bán vi phạm.
            7. Ai chịu thuế, phí công chứng, lệ phí trước bạ và chi phí phát sinh.
            8. Cam kết bàn giao giấy tờ gốc, bàn giao đất/nhà, tài sản kèm theo.

            Không nên đặt cọc lớn khi chưa xem sổ gốc và chưa kiểm tra quy hoạch.
            """;

            return """
            **Quy trình giao dịch nhà đất an toàn**

            1. Xác định nhu cầu và chọn tin phù hợp.
            2. Xem thực tế tài sản, đối chiếu vị trí/diện tích/đường vào.
            3. Kiểm tra sổ gốc, chủ sở hữu, quy hoạch, tranh chấp, thế chấp.
            4. Thương lượng giá, thuế phí, bàn giao và mốc thanh toán.
            5. Đặt cọc bằng văn bản rõ điều kiện hoàn cọc/phạt cọc.
            6. Công chứng hợp đồng chuyển nhượng.
            7. Kê khai thuế, lệ phí và nộp hồ sơ sang tên.
            8. Nhận sổ đã sang tên và bàn giao tài sản.

            Không nên chuyển hết tiền trước khi có cơ chế bảo vệ rõ ràng.
            """;
        }

        private static string LoanAnswer(Dictionary<string, string> slots, string n)
        {
            LoanInput input = ExtractLoanInput(n, slots);
            string intro = "Em tính theo hướng **tham khảo dòng tiền**, không thay ngân hàng thẩm định hồ sơ.";
            string disclaimer = "Kết quả chỉ để Anh/Chị ước lượng ban đầu. Ngân hàng còn xét pháp lý tài sản, giá trị thẩm định, lịch sử tín dụng, nợ hiện có, người phụ thuộc, nghề nghiệp, độ ổn định thu nhập và chính sách từng thời điểm.";

            if (ContainsAny(n, "lai suat tha noi", "lãi suất thả nổi"))
            {
                return """
                **Lãi suất thả nổi có rủi ro gì?**

                Lãi suất thả nổi có thể tăng sau thời gian ưu đãi, làm tiền trả hằng tháng cao hơn dự kiến. Khi vay, Anh/Chị nên hỏi ngân hàng thật rõ:

                1. Lãi suất ưu đãi trong bao lâu.
                2. Sau ưu đãi tính bằng công thức nào: lãi tham chiếu + biên độ bao nhiêu.
                3. Chu kỳ điều chỉnh lãi suất: 3 tháng, 6 tháng hay 12 tháng.
                4. Phí trả nợ trước hạn.
                5. Nếu lãi tăng 2-4%/năm thì tiền trả tháng còn chịu được không.

                Không nên chỉ nhìn mức lãi ưu đãi ban đầu. Nên tính kịch bản xấu trước khi ký hợp đồng vay.
                """;
            }

            if (ContainsAny(n, "mua dat co so do", "co so do thi ngan hang", "ngan hang co cho vay", "co so do ngan hang co cho vay"))
            {
                return """
                **Mua đất có sổ đỏ/sổ hồng thì ngân hàng có cho vay không?**

                Có sổ là điều kiện thuận lợi, nhưng **không có nghĩa chắc chắn vay được**. Ngân hàng thường kiểm tra:

                1. Sổ thật, đúng chủ sở hữu, không tranh chấp, không bị ngăn chặn giao dịch.
                2. Mục đích sử dụng đất, quy hoạch, lộ giới và khả năng sang tên.
                3. Tài sản có dễ định giá, dễ thanh khoản không.
                4. Giá trị thẩm định của ngân hàng, thường có thể thấp hơn giá mua.
                5. Thu nhập, lịch sử tín dụng và nợ hiện có của người vay.

                Trước khi đặt cọc lớn, Anh/Chị nên hỏi ngân hàng thẩm định sơ bộ tài sản và hồ sơ vay trước.
                """;
            }

            if (ContainsAny(n, "dat chua co tho cu", "chua co tho cu", "dat nong nghiep ngan hang"))
            {
                return """
                **Đất chưa có thổ cư thì ngân hàng có nhận thế chấp không?**

                Có thể khó vay hơn hoặc tỷ lệ vay thấp hơn, tùy loại đất, quy hoạch, pháp lý, vị trí và chính sách từng ngân hàng. Một số ngân hàng không thích tài sản khó thanh khoản hoặc chưa có mục đích đất ở.

                Anh/Chị nên kiểm tra:
                1. Trên sổ ghi loại đất gì.
                2. Có chuyển mục đích lên đất ở được không.
                3. Có vướng quy hoạch/lộ giới không.
                4. Ngân hàng định giá được bao nhiêu.
                5. Tỷ lệ cho vay trên giá trị thẩm định là bao nhiêu.

                Không nên đặt cọc lớn trước khi ngân hàng trả lời sơ bộ.
                """;
            }

            if (ContainsAny(n, "vay 50", "vay 70", "50%", "70%", "nen vay 50", "nen vay 70"))
            {
                return """
                **Nên vay 50% hay 70% giá trị nhà đất?**

                Nếu mới mua lần đầu hoặc thu nhập chưa thật dư dả, **vay khoảng 50% thường an toàn hơn 70%**.

                - Vay 50%: áp lực trả nợ thấp hơn, chịu được biến động lãi suất tốt hơn, ít rủi ro phải bán gấp.
                - Vay 70%: cần dòng tiền mạnh và ổn định, dễ áp lực khi lãi suất tăng hoặc có chi phí phát sinh.
                - Dù vay mức nào, nên giữ quỹ dự phòng 3-6 tháng chi phí sinh hoạt và tiền trả nợ.

                Quy tắc an toàn: tiền trả nợ hằng tháng nên nằm trong mức gia đình chịu được sau khi trừ chi phí sinh hoạt, không nên tính quá sát.
                """;
            }

            if (input.LoanAmount.HasValue || input.PropertyPrice.HasValue || input.MonthlyIncome.HasValue || ContainsAny(n, "ap luc", "vay toi da", "vay duoc bao nhieu", "vay 1 ty", "trong 20 nam", "20 nam", "15 nam", "25 nam"))
            {
                return BuildBankCalculationAnswer(input, n);
            }

            return $"""
            **Bộ tính vay ngân hàng tham khảo**

            {intro}

            Để tính sát hơn, Anh/Chị gửi theo mẫu:
            - Giá nhà/đất muốn mua.
            - Vốn tự có.
            - Thu nhập ổn định mỗi tháng.
            - Chi phí cố định mỗi tháng nếu có.
            - Số tiền muốn vay hoặc tỷ lệ vay.
            - Thời hạn vay mong muốn.

            Ví dụ: “Tôi có 500 triệu, mua nhà 1,5 tỷ, thu nhập 25 triệu/tháng, muốn vay 20 năm”.

            {disclaimer}
            """;
        }


        private sealed class LoanInput
        {
            public decimal? PropertyPrice { get; set; }
            public decimal? CashAvailable { get; set; }
            public decimal? LoanAmount { get; set; }
            public decimal? MonthlyIncome { get; set; }
            public decimal? FixedMonthlyExpense { get; set; }
            public int Years { get; set; } = 20;
            public decimal AnnualRate { get; set; } = 10m;
        }

        private sealed class LoanScenarioResult
        {
            public decimal AnnualRate { get; set; }
            public decimal EqualPayment { get; set; }
            public decimal FirstDecreasingPayment { get; set; }
            public decimal SafeIncomeMin { get; set; }
        }

        private static LoanInput ExtractLoanInput(string n, Dictionary<string, string> slots)
        {
            LoanInput input = new();

            input.MonthlyIncome = FirstMoneyAfter(n, "thu nhap", "luong", "lương") ?? DecimalSlot(slots, "monthly_income");
            input.CashAvailable = FirstMoneyAfter(n, "von tu co", "von", "co san", "co ", "tien mat", "tiet kiem") ?? DecimalSlot(slots, "cash_available");
            input.PropertyPrice = ExtractPropertyPrice(n) ?? DecimalSlot(slots, "budget_max");
            input.LoanAmount = ExtractExplicitLoanAmount(n);

            Match years = Regex.Match(n, @"(?<y>\d{1,2})\s*(?:nam|năm)", RegexOptions.IgnoreCase);
            if (years.Success && int.TryParse(years.Groups["y"].Value, out int y) && y >= 1 && y <= 35)
                input.Years = y;

            decimal? rate = ExtractInterestRate(n);
            if (rate.HasValue && rate.Value > 0 && rate.Value < 40) input.AnnualRate = rate.Value;

            if (!input.LoanAmount.HasValue && input.PropertyPrice.HasValue && input.CashAvailable.HasValue && input.PropertyPrice.Value > input.CashAvailable.Value)
                input.LoanAmount = input.PropertyPrice.Value - input.CashAvailable.Value;

            return input;
        }

        private static decimal? ExtractPropertyPrice(string n)
        {
            string normalized = Normalize(n);
            string[] anchors = { "mua nha", "mua dat", "mua can ho", "nha", "dat", "can ho", "bds", "bat dong san", "gia" };
            foreach (string anchor in anchors)
            {
                int idx = normalized.IndexOf(Normalize(anchor), StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string part = normalized[idx..];
                List<decimal> values = MoneyValues(part);
                if (values.Any()) return values.First();
            }

            List<decimal> all = MoneyValues(normalized);
            if (all.Count >= 2) return all.Max();
            return null;
        }

        private static decimal? ExtractExplicitLoanAmount(string n)
        {
            string normalized = Normalize(n);
            foreach (string anchor in new[] { "muon vay", "can vay", "vay" })
            {
                int idx = normalized.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                List<decimal> values = MoneyValues(normalized[idx..]);
                if (values.Any()) return values.First();
            }
            return null;
        }

        private static decimal? ExtractInterestRate(string n)
        {
            Match m = Regex.Match(n, @"(?<r>\d+(?:[\.,]\d+)?)\s*%\s*(?:/nam|nam|moi nam)?", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (decimal.TryParse(m.Groups["r"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal r))
                return r;
            return null;
        }

        private static string BuildBankCalculationAnswer(LoanInput input, string n)
        {
            decimal? loan = input.LoanAmount;
            if (!loan.HasValue && input.MonthlyIncome.HasValue && ContainsAny(n, "vay toi da", "mua nha toi da", "toi da bao nhieu", "vay duoc bao nhieu", "nen vay bao nhieu", "nen vay mua nha toi da", "thu nhap"))
            {
                decimal safeMonthly = input.MonthlyIncome.Value * 0.40m;
                decimal estimatedLoan = EstimatePrincipalFromPayment(safeMonthly, 10m, input.Years);
                return $"""
                **Ước tính khoản vay tối đa theo thu nhập**

                Em tạm lấy ngưỡng an toàn là khoảng **35–45% thu nhập ổn định** để trả nợ.

                - Thu nhập: **{Price(input.MonthlyIncome.Value)}/tháng**
                - Khoản trả nợ tham khảo nên quanh: **{Price(input.MonthlyIncome.Value * 0.35m)} – {Price(input.MonthlyIncome.Value * 0.45m)}/tháng**
                - Nếu vay khoảng **{input.Years} năm**, lãi suất giả định **10%/năm**, khoản vay tương ứng tham khảo khoảng **{Price(estimatedLoan)}**

                Đây không phải hạn mức ngân hàng cam kết. Hạn mức thật còn phụ thuộc chi phí sinh hoạt, nợ hiện có, người phụ thuộc, lịch sử tín dụng và pháp lý tài sản.
                """;
            }

            if (!loan.HasValue)
            {
                return """
                Em chưa đủ dữ liệu để tính số tiền trả ngân hàng. Anh/Chị cho em thêm ít nhất 3 thông tin:
                1. Giá nhà/đất muốn mua.
                2. Vốn tự có.
                3. Thu nhập ổn định mỗi tháng.
                4. Thời hạn vay mong muốn nếu có.

                Ví dụ: “Tôi có 500 triệu, mua nhà 1,5 tỷ, thu nhập 25 triệu/tháng, vay 20 năm”.
                """;
            }

            List<LoanScenarioResult> scenarios = new();
            foreach (decimal r in new[] { 8m, 10m, 12m })
            {
                scenarios.Add(new LoanScenarioResult
                {
                    AnnualRate = r,
                    EqualPayment = EqualMonthlyPayment(loan.Value, r, input.Years),
                    FirstDecreasingPayment = FirstDecreasingPayment(loan.Value, r, input.Years),
                    SafeIncomeMin = EqualMonthlyPayment(loan.Value, r, input.Years) / 0.40m
                });
            }

            string cashLine = input.CashAvailable.HasValue ? $"\n- Vốn tự có: **{Price(input.CashAvailable.Value)}**" : "";
            string propertyLine = input.PropertyPrice.HasValue ? $"\n- Giá tài sản dự kiến: **{Price(input.PropertyPrice.Value)}**" : "";
            string incomeLine = input.MonthlyIncome.HasValue ? $"\n- Thu nhập ổn định: **{Price(input.MonthlyIncome.Value)}/tháng**" : "";
            string verdict = BuildLoanVerdict(input, scenarios);

            string scenarioLines = string.Join("\n", scenarios.Select(x =>
                $"- Lãi {x.AnnualRate:0.#}%/năm: trả góp đều khoảng **{Price(x.EqualPayment)}/tháng**; dư nợ giảm dần tháng đầu khoảng **{Price(x.FirstDecreasingPayment)}**; thu nhập an toàn nên từ **{Price(x.SafeIncomeMin)}/tháng** trở lên."));

            return $"""
            **Bộ tính vay ngân hàng tham khảo**

            Em tính chậm theo 3 bước: xác định số tiền vay → ước tính tiền trả tháng → so với thu nhập an toàn.

            **Dữ liệu em hiểu**
            {propertyLine}{cashLine}
            - Số tiền vay ước tính: **{Price(loan.Value)}**
            - Thời hạn vay: **{input.Years} năm**{incomeLine}

            **Kịch bản trả nợ tham khảo**
            {scenarioLines}

            **Kết luận sơ bộ**
            {verdict}

            **Cần hỏi ngân hàng trước khi đặt cọc lớn**
            1. Ngân hàng định giá tài sản bao nhiêu, có thấp hơn giá mua không.
            2. Tỷ lệ cho vay tối đa trên giá trị thẩm định.
            3. Lãi suất ưu đãi, lãi sau ưu đãi, biên độ và chu kỳ thả nổi.
            4. Phí trả nợ trước hạn, phí bảo hiểm nếu có.
            5. Hồ sơ cần chuẩn bị và khả năng duyệt sơ bộ.

            Đây là tính toán tham khảo, không phải cam kết duyệt vay.
            """;
        }

        private static string BuildLoanVerdict(LoanInput input, List<LoanScenarioResult> scenarios)
        {
            if (!input.MonthlyIncome.HasValue)
                return "Chưa có thu nhập tháng nên em chưa kết luận áp lực nặng hay nhẹ. Anh/Chị nên bổ sung thu nhập và chi phí cố định để đánh giá sát hơn.";

            decimal income = input.MonthlyIncome.Value;
            decimal low = scenarios.First(x => x.AnnualRate == 10m).EqualPayment;
            decimal ratio = low / Math.Max(income, 1);

            if (ratio <= 0.35m)
                return $"Khoản vay này **khá an toàn hơn** nếu thu nhập {Price(income)}/tháng ổn định và không có nhiều nợ khác, vì tiền trả ước tính chiếm khoảng {(ratio * 100):0.#}% thu nhập.";
            if (ratio <= 0.45m)
                return $"Khoản vay này **ở mức cần cân nhắc kỹ**, vì tiền trả ước tính chiếm khoảng {(ratio * 100):0.#}% thu nhập. Nên giữ quỹ dự phòng 3–6 tháng và thử kịch bản lãi tăng.";
            if (ratio <= 0.60m)
                return $"Khoản vay này **áp lực cao**, vì tiền trả ước tính chiếm khoảng {(ratio * 100):0.#}% thu nhập. Nên tăng vốn tự có, giảm giá trị tài sản hoặc kéo dài thời hạn vay nếu phù hợp.";
            return $"Khoản vay này **rất áp lực** so với thu nhập hiện tại, vì tiền trả ước tính chiếm khoảng {(ratio * 100):0.#}% thu nhập. Không nên quyết định nếu chưa có thêm nguồn thu hoặc vốn tự có.";
        }

        private static decimal EqualMonthlyPayment(decimal principal, decimal annualRatePercent, int years)
        {
            int months = Math.Max(1, years * 12);
            decimal monthlyRate = annualRatePercent / 100m / 12m;
            if (monthlyRate <= 0) return principal / months;
            double r = (double)monthlyRate;
            double p = (double)principal;
            double pow = Math.Pow(1 + r, months);
            return (decimal)(p * r * pow / (pow - 1));
        }

        private static decimal FirstDecreasingPayment(decimal principal, decimal annualRatePercent, int years)
        {
            int months = Math.Max(1, years * 12);
            decimal principalPerMonth = principal / months;
            decimal firstInterest = principal * (annualRatePercent / 100m / 12m);
            return principalPerMonth + firstInterest;
        }

        private static decimal EstimatePrincipalFromPayment(decimal monthlyPayment, decimal annualRatePercent, int years)
        {
            int months = Math.Max(1, years * 12);
            decimal monthlyRate = annualRatePercent / 100m / 12m;
            if (monthlyRate <= 0) return monthlyPayment * months;
            double r = (double)monthlyRate;
            double pay = (double)monthlyPayment;
            double pv = pay * (1 - Math.Pow(1 + r, -months)) / r;
            return (decimal)pv;
        }

        private static string NormalizeDisplayMoney(string value, string fallback)
        {
            string v = value.Trim();
            if (ContainsAny(v, "ty", "tỷ", "ti", "tỉ", "trieu", "triệu", "tr")) return v;
            if (fallback.Contains("tỷ", StringComparison.OrdinalIgnoreCase) || fallback.Contains("ty", StringComparison.OrdinalIgnoreCase)) return v + " tỷ";
            if (fallback.Contains("triệu", StringComparison.OrdinalIgnoreCase) || fallback.Contains("tr", StringComparison.OrdinalIgnoreCase)) return v + " triệu";
            return v;
        }

        private static string PostingAnswer(string n)
        {
            if (ContainsAny(n, "bao loi", "bao lai", "chac chan tang gia", "cam ket loi nhuan", "co nen ghi"))
                return """
                **Có nên ghi “bao lời, chắc chắn tăng giá” trong tin đăng không?**

                Không nên ghi như vậy nếu không có cơ sở pháp lý/tài liệu chứng minh rõ ràng. Những câu kiểu “bao lời”, “chắc chắn tăng giá”, “đầu tư là thắng” dễ làm tin thiếu trung thực, bị người mua phản ứng, bị báo cáo vi phạm hoặc bị admin từ chối.

                Nên viết an toàn hơn:
                - “Phù hợp tham khảo đầu tư dài hạn” nếu đúng thực tế.
                - “Khu vực có tiềm năng nhờ hạ tầng/tiện ích xung quanh” nhưng không cam kết lợi nhuận.
                - “Giá còn thương lượng, pháp lý/diện tích/vị trí như mô tả”.

                Nguyên tắc: mô tả đúng sự thật, không cam kết lợi nhuận, không che giấu rủi ro pháp lý/quy hoạch.
                """;

            if (ContainsAny(n, "ban nhanh", "khong muon bi ep gia", "bi ep gia", "trinh bay tin", "de co khach", "de co khach hang"))
                return """
                **Muốn bán nhanh nhưng không bị ép giá, nên trình bày tin như thế nào?**

                Anh/Chị nên làm tin đăng rõ, đủ dữ liệu và có cơ sở giá để khách khó ép vô lý:

                1. Tiêu đề ghi đúng loại BĐS, khu vực, diện tích, pháp lý và điểm mạnh chính.
                2. Mô tả rõ: diện tích, đường vào, hướng, tiện ích, pháp lý, hiện trạng, lý do bán nếu phù hợp.
                3. Ảnh thật: mặt tiền/lối vào, đường trước nhà/đất, tổng thể, giấy tờ che thông tin nhạy cảm nếu cần.
                4. Giá nên có căn cứ: so với tin cùng khu, cùng diện tích, cùng pháp lý.
                5. Ghi “có thương lượng cho khách thiện chí” thay vì giảm giá sâu ngay từ đầu.
                6. Chuẩn bị sẵn câu trả lời về sổ, quy hoạch, đường vào, thuế phí và mốc công chứng.

                Không nên phóng đại như “bao lời”, “chắc chắn tăng giá”, vì dễ mất uy tín và bị báo cáo.
                """;

            if (ContainsAny(n, "3 tieu de", "ba tieu de", "viet tieu de")) return """
            Em gợi ý 3 tiêu đề trung thực, chuyên nghiệp:
            1. Bán đất Diên Khánh 100m², giá 1,2 tỷ, pháp lý rõ.
            2. Cần bán lô đất 100m² tại Diên Khánh, đường thuận tiện, giá thương lượng.
            3. Bán đất phù hợp xây nhà tại Diên Khánh, diện tích 100m², sổ riêng nếu đúng thực tế.

            Anh/Chị nên bổ sung đúng khu vực, đường vào, pháp lý, tiện ích và ảnh thật để tin đáng tin hơn.
            """;

            if (ContainsAny(n, "so rieng", "duong o to", "gan cho", "dang tin the nao", "de co khach"))
                return """
                **Cách đăng tin bán đất có sổ riêng, đường ô tô, gần chợ**

                Tiêu đề nên nêu đúng điểm mạnh:
                “Bán đất sổ riêng, đường ô tô, gần chợ, phù hợp xây nhà ở lâu dài”

                Nội dung nên có:
                1. Diện tích, ngang/dài nếu có.
                2. Vị trí cụ thể đến mức được phép công khai.
                3. Đường trước đất: ô tô/xe máy, bê tông/nhựa, rộng khoảng bao nhiêu.
                4. Pháp lý: sổ riêng/sổ hồng/sổ đỏ, loại đất nếu biết.
                5. Tiện ích: gần chợ, trường, khu dân cư, trung tâm.
                6. Giá bán, có thương lượng hay không.
                7. Ảnh thật và thông tin liên hệ rõ.

                Không nên ghi quá mức như “bao lời”, “chắc chắn tăng giá”. Nên để khách tin vì thông tin đầy đủ và trung thực.
                """;

            return "Tin đăng tốt cần tiêu đề rõ, mô tả trung thực, ảnh thật, giá minh bạch, pháp lý ghi đúng thực tế. Không nên ghi 'bao lời', 'chắc chắn tăng giá', 'pháp lý 100%' nếu chưa có cơ sở. Nếu tin bị từ chối, thường do thiếu thông tin, sai loại hình/khu vực, ảnh kém, trùng tin hoặc nội dung phóng đại.";
        }
        private static string ProjectAnswer(string n) => "Khi mua dự án, cần kiểm tra chủ đầu tư, quyền sử dụng đất, quy hoạch, giấy phép xây dựng nếu cần, điều kiện mở bán, bảo lãnh ngân hàng nếu áp dụng, hợp đồng mẫu, tiến độ thanh toán, điều khoản chậm bàn giao, phí quản lý, quỹ bảo trì và điều kiện cấp sổ. Không nên tin cam kết lợi nhuận nếu chưa đọc kỹ điều kiện bằng văn bản.";
        private static string CareAnswer(string n, Dictionary<string, string> slots) => Complaint(n) ? "Nếu muốn báo cáo tin vi phạm, Anh/Chị bấm nút Báo cáo vi phạm trên trang chi tiết tin, chọn lý do, mô tả dấu hiệu sai phạm và gửi kèm bằng chứng nếu có. Nếu đã chuyển tiền, hãy lưu lại tin nhắn, biên lai, số tài khoản/số điện thoại và cân nhắc liên hệ cơ quan chức năng." : Appointment(n) ? "Để đặt lịch xem, Anh/Chị mở trang chi tiết tin, bấm Đặt lịch xem/Yêu cầu tư vấn, nhập họ tên, số điện thoại, thời gian muốn xem và ghi chú. Trước khi đi xem nên chuẩn bị câu hỏi về pháp lý, đường vào, hiện trạng và giá." : "Anh/Chị vui lòng mô tả vấn đề, kèm mã tin/mã giao dịch/tài khoản và ảnh chụp màn hình nếu có để bộ phận hỗ trợ kiểm tra chính xác.";
        private static string MarketAnswer(string n = "")
        {
            if (ContainsAny(n, "ngan sach gioi han", "giam yeu cau dien tich hay vi tri", "giam dien tich hay vi tri", "nen giam dien tich hay vi tri"))
                return """
                **Ngân sách giới hạn thì nên giảm diện tích hay vị trí?**

                Không có đáp án cố định cho mọi người. Cách chọn an toàn hơn là xem mục đích chính:

                1. Nếu mua **để ở lâu dài**: thường nên ưu tiên vị trí đủ thuận tiện, pháp lý rõ, hạ tầng ổn; có thể giảm diện tích vừa phải.
                2. Nếu mua **để đầu tư/giữ tài sản**: xem thanh khoản, quy hoạch, dân cư thật và khả năng tăng nhu cầu; không nên chỉ chọn diện tích lớn nhưng quá xa/kém thanh khoản.
                3. Nếu mua **để xây nhà**: phải kiểm tra loại đất, quy hoạch, đường vào, điện nước, chi phí xây dựng sau mua.
                4. Nếu ngân sách quá sát: nên giữ quỹ dự phòng, không dồn hết tiền vào đất rồi thiếu tiền xây/sửa.

                Kết luận sơ bộ: với người mua lần đầu, em thường ưu tiên **pháp lý rõ + vị trí sống được + chi phí sau mua kiểm soát được**, rồi mới tối ưu diện tích.
                """;

            if (ContainsAny(n, "dat mat tien gia cao", "mat tien gia cao", "dat mat tien", "co dang mua", "dang mua khong"))
                return """
                **Đất mặt tiền giá cao hơn nhiều có đáng mua không?**

                Đáng mua khi mặt tiền tạo ra giá trị thật: dễ kinh doanh, dễ cho thuê, dễ bán lại, đường lớn hợp pháp, khu dân cư có nhu cầu và pháp lý rõ. Nhưng không đáng mua nếu giá cao chỉ vì “mặt tiền” mà khu vực ít nhu cầu, đường quy hoạch chưa chắc, pháp lý chưa rõ hoặc dòng tiền không bù được phần chênh.

                Anh/Chị nên kiểm tra:
                1. Mặt tiền là đường hiện hữu hợp pháp hay đường dự kiến/quy hoạch.
                2. Giá chênh bao nhiêu so với lô trong hẻm cùng khu.
                3. Có thể khai thác kinh doanh/cho thuê thật không.
                4. Có vướng lộ giới, hành lang, quy hoạch mở đường không.
                5. Nếu sau này bán lại, nhóm khách mua có đủ rộng không.

                Kết luận: mua mặt tiền chỉ hợp lý khi **pháp lý rõ + vị trí có nhu cầu thật + giá chênh có lý do**.
                """;

            if (ContainsAny(n, "tin nay gia re", "gia re co so duong nho", "gia re", "duong nho", "hem nho", "co so"))
                return """
                **Tin giá rẻ, có sổ nhưng đường nhỏ có nên mua không?**

                Em chưa có đủ dữ liệu để kết luận “nên mua”. Trường hợp này cần kiểm tra kỹ vì giá rẻ thường đi kèm một lý do nào đó.

                Nên hỏi và kiểm tra:
                1. Đường vào có hợp pháp không, có tranh chấp lối đi không.
                2. Đường nhỏ có ảnh hưởng xây dựng, vận chuyển vật liệu, PCCC, sinh hoạt và bán lại không.
                3. Sổ ghi đúng diện tích, mục đích sử dụng đất và chủ sở hữu không.
                4. Có vướng quy hoạch/lộ giới/hành lang bảo vệ không.
                5. Lý do bán rẻ là gì, giá có thấp bất thường so với các lô xung quanh không.

                Kết luận sơ bộ: **có thể xem xét**, nhưng không nên cọc ngay. Chỉ đi tiếp nếu đường vào hợp pháp, pháp lý sạch, giá thấp có lý do hợp lý và phù hợp mục đích sử dụng.
                """;

            if (ContainsAny(n, "dat mat tien hay nha xay san", "vua o vua kinh doanh", "kinh doanh nho", "chon dat mat tien", "nha xay san"))
                return """
                **Vừa ở vừa kinh doanh nhỏ nên chọn đất mặt tiền hay nhà xây sẵn?**

                Nếu cần kinh doanh sớm, **nhà xây sẵn/nhà mặt tiền** thường thực tế hơn vì có thể khai thác ngay, dễ tính dòng tiền và chi phí ban đầu rõ hơn. Nếu muốn tự thiết kế lâu dài, **đất mặt tiền** linh hoạt hơn nhưng phải kiểm tra được xây dựng, chi phí xây, thời gian xin phép và rủi ro phát sinh.

                Nên ưu tiên:
                1. Pháp lý rõ, được phép ở và kinh doanh phù hợp.
                2. Mặt tiền/đường vào đủ thuận tiện, có nhu cầu dân cư thật.
                3. Chỗ đậu xe, tiếng ồn, PCCC/giấy phép nếu kinh doanh ngành có điều kiện.
                4. Tổng chi phí sau mua: sửa chữa, xây dựng, nội thất, thuế phí.
                5. Thanh khoản sau này nếu cần bán lại.

                Với người mua lần đầu, em nghiêng về phương án **pháp lý rõ + vị trí có nhu cầu thật + chi phí sau mua kiểm soát được**, không nên chỉ chọn vì rẻ.
                """;

            if (ContainsAny(n, "900 trieu", "900tr", "co kha thi", "kha thi khong"))
                return "Với ngân sách khoảng 900 triệu để mua đất ở Khánh Hòa và sau này xây nhà, vẫn có thể khả thi ở một số khu vực xa trung tâm hơn, nhưng cần rất thực tế: Nha Trang thường khó hơn, còn Diên Khánh, Ninh Hòa, Cam Lâm, Vạn Ninh hoặc khu vực xa trung tâm có thể dễ tìm hơn tùy dữ liệu. Nên ưu tiên sổ riêng, đất ở hoặc có khả năng xây dựng, đường vào rõ và không vướng quy hoạch. Nếu muốn em lọc tin, Anh/Chị nên chọn thêm khu vực cụ thể để tránh tìm quá rộng toàn tỉnh.";

            if (ContainsAny(n, "gia re", "so dinh quy hoach", "so vuong quy hoach", "hoi nguoi ban gi"))
                return "Khi gặp đất giá rẻ nhưng sợ dính quy hoạch, Anh/Chị nên hỏi người bán: đất có sổ gốc không, số thửa/tờ bản đồ là gì, mục đích sử dụng đất, có nằm quy hoạch/lộ giới/kế hoạch thu hồi không, có tranh chấp/thế chấp không, lý do bán rẻ, đường vào có hợp pháp không và có cho kiểm tra quy hoạch trước khi cọc không. Hợp đồng cọc phải ghi điều kiện hoàn cọc nếu thông tin quy hoạch/pháp lý không đúng.";

            if (ContainsAny(n, "vi tri dien tich hay phap ly", "uu tien phap ly hay vi tri", "phap ly hay vi tri"))
                return "Nếu mua lần đầu, pháp lý là điều kiện nền tảng: sổ gốc, chủ sở hữu, quy hoạch, tranh chấp, thế chấp và điều kiện sang tên phải rõ trước. Sau đó mới cân đối vị trí và diện tích. Vị trí tốt nhưng pháp lý rủi ro thì không nên cọc; pháp lý rõ nhưng vị trí quá bất tiện cũng ảnh hưởng ở thật và bán lại.";

            return "Nếu mua để ở: ưu tiên pháp lý rõ, hạ tầng, tiện ích và môi trường sống. Nếu đầu tư/bán lại: xem thanh khoản, quy hoạch, khả năng cho thuê, dòng tiền, đường vào và nhu cầu thật của khu vực. Giá rẻ bất thường cần kiểm tra kỹ pháp lý, quy hoạch, tranh chấp, đường vào và lý do bán. Em không dự đoán chắc chắn tăng giá.";
        }
        private static string GeneralAnswer() => "Em chưa đủ dữ liệu để trả lời chắc chắn câu này nên em không đoán bừa. Anh/Chị vui lòng nói rõ đang hỏi về: tìm mua/thuê, pháp lý, giao dịch, vay ngân hàng, đăng tin, dự án hay tin đang xem. Nếu hỏi về một tin cụ thể, hãy gửi tiêu đề, giá, diện tích, vị trí và pháp lý hiển thị để em phân tích đúng hơn.";

        private async Task<string> AiOrFallbackAsync(string original, string normalized, Scenario sc, Dictionary<string, string> slots, SlotPlan plan, PageInfo page, List<AIChatMessage> history)
        {
            string fallback = GeneralAnswer();
            try
            {
                string knowledge = await KnowledgeContextAsync(sc);
                string prompt = $"""
                Câu hỏi mới nhất: {original}
                Scenario: {sc.Name}
                Intent: {sc.Intent}
                Slots: {JsonSerializer.Serialize(CleanSlots(slots))}
                Quy tắc: không đề xuất tin nếu thiếu loại BĐS + khu vực + ngân sách; không bịa pháp lý, quy hoạch, lãi suất; hỏi thêm nếu thiếu dữ liệu.
                Tri thức liên quan: {knowledge}
                Trả lời bằng tiếng Việt, ngắn gọn, đúng trọng tâm.
                """;
                AIChatCompletionResult rs = await _aiClient.GenerateAsync(new AIChatCompletionRequest { SystemPrompt = SystemPrompt(), UserPrompt = prompt, Temperature = 0.12, MaxOutputTokens = 2400, UseAnswerModel = true });
                if (rs.Success && !string.IsNullOrWhiteSpace(rs.Text)) return Clean(rs.Text);
                return fallback;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI fallback error");
                return fallback;
            }
        }

        private async Task<string> KnowledgeContextAsync(Scenario sc)
        {
            List<string> cats = new() { "Core", "Guardrail", "Fallback", sc.Name };
            if (sc.Name == "Buy") cats.AddRange(new[] { "Buy", "Legal", "Search", "Market" });
            if (sc.Name == "Rent") cats.AddRange(new[] { "Rent", "Search" });
            cats = cats.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var arts = await _context.Set<AIKnowledgeArticle>().AsNoTracking().Where(x => x.IsPublished && cats.Contains(x.Category)).OrderByDescending(x => x.UpdatedAt).Take(8).ToListAsync();
            StringBuilder sb = new();
            foreach (var a in arts) sb.AppendLine($"## {a.Title}\n{Trim(a.Content, 2200)}");
            return Trim(sb.ToString(), 12000);
        }

        private static string SystemPrompt() => """
        Bạn là Trợ lý AI BĐS Khánh Hòa. Luôn xử lý theo clarify-first.
        Muốn đề xuất tin phải có đủ mua/thuê + loại BĐS + khu vực + ngân sách.
        Nếu hỏi mua an toàn/pháp lý/vay/giao dịch thì trả checklist, không tự tìm SQL.
        Không bịa giá, tin, pháp lý, quy hoạch, lãi suất. Không cam kết chắc chắn.
        Nếu không chắc, nói chưa đủ dữ liệu và hỏi thêm tối đa 2-4 ý.
        """;

        private static string Welcome() => "Chào Anh/Chị! Em là Trợ lý AI BĐS Khánh Hòa. Em có thể hỗ trợ tìm mua/thuê, pháp lý cơ bản, giao dịch, vay vốn, đăng tin, dự án và phân tích tin đang xem. Với nhu cầu tìm tin, em sẽ hỏi đủ thông tin trước rồi mới đề xuất để tránh sai.";
        private static string UnsafeAnswer() => "Em không thể hỗ trợ làm giả giấy tờ, lách luật, trốn thuế, che giấu tranh chấp/quy hoạch/thế chấp, viết tin gian dối hoặc hack hệ thống. Em có thể hướng dẫn giao dịch hợp pháp và an toàn hơn.";
        private static string OffTopicAnswer(string n) => ContainsAny(n, "ngu", "tra loi lai") ? "Em xin lỗi vì câu trả lời trước chưa đúng ý. Anh/Chị gửi lại câu hỏi về bất động sản để em xử lý lại sát hơn." : "Em chủ yếu hỗ trợ bất động sản: mua, thuê, pháp lý, giao dịch, vay vốn, đăng tin, dự án và chăm sóc khách hàng.";

        private static string NeedSummary(Dictionary<string, string> slots, Scenario sc)
        {
            List<string> p = new();
            p.Add(slots.GetValueOrDefault("deal_type", sc.Name == "Rent" ? "Thuê" : "Mua").ToLower(Vi));
            if (slots.TryGetValue("property_type", out var t)) p.Add(t.ToLower(Vi));
            if (slots.TryGetValue("area_name", out var a)) p.Add("tại " + a);
            if (slots.TryGetValue("budget_max", out var b) && decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var bd)) p.Add("dưới " + Price(bd));
            if (slots.TryGetValue("rent_max", out var r) && decimal.TryParse(r, NumberStyles.Any, CultureInfo.InvariantCulture, out var rd)) p.Add("dưới " + Price(rd) + "/tháng");
            return "Em hiểu nhu cầu ban đầu của Anh/Chị là " + string.Join(" ", p) + ".";
        }

        private static List<object> Cards(List<Property> props, Dictionary<string, string> slots, Scenario sc) => props
            .GroupBy(p => p.PropertyID)
            .Select(g => g.First())
            .Take(MaxCards)
            .Select(p => new { propertyID = p.PropertyID, title = p.Title, price = Price(p.Price), areaSize = p.AreaSize.HasValue ? p.AreaSize.Value.ToString("0.##", Vi) + " m²" : "Chưa cập nhật", areaName = p.Ward?.Area?.AreaName ?? "Khánh Hòa", wardName = p.Ward?.WardName ?? "", propertyTypeName = p.PropertyType?.TypeName ?? "Chưa cập nhật", image = string.IsNullOrWhiteSpace(p.MainImage) ? "/images/no-image.png" : p.MainImage, link = "/Property/Details/" + p.PropertyID, location = Location(p), reasonMatched = Reason(p, slots, sc) })
            .Cast<object>()
            .ToList();
        private static string Reason(Property p, Dictionary<string, string> slots, Scenario sc) { List<string> r = new(); if (slots.TryGetValue("area_name", out var a) && MatchArea(p, Normalize(a))) r.Add("đúng khu vực"); if (slots.TryGetValue("property_type", out var t) && MatchType(p, Normalize(t))) r.Add("đúng loại bất động sản"); if (slots.TryGetValue("budget_max", out var b) && decimal.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var bd) && p.Price.HasValue && p.Price.Value <= bd) r.Add("giá trong ngân sách"); return r.Any() ? string.Join(", ", r) + "." : "phù hợp một phần với tiêu chí đã nêu."; }
        private static string BuildPropertyContext(List<Property> props, Dictionary<string, string> slots, Scenario sc) { StringBuilder sb = new(); foreach (var p in props) sb.AppendLine($"ID {p.PropertyID}: {p.Title} | {Price(p.Price)} | {p.AreaSize:0.##}m² | {Location(p)} | /Property/Details/{p.PropertyID}"); return sb.ToString(); }
        private static string Location(Property p) => string.Join(", ", new[] { p.AddressDetail, p.Ward?.WardName, p.Ward?.Area?.AreaName }.Where(x => !string.IsNullOrWhiteSpace(x)));
        private static string Price(decimal? price) => !price.HasValue || price.Value <= 0 ? "Thỏa thuận" : Price(price.Value);
        private static string Price(decimal price) => price >= 1_000_000_000m ? (price / 1_000_000_000m).ToString("0.##", Vi) + " tỷ" : price >= 1_000_000m ? (price / 1_000_000m).ToString("0.##", Vi) + " triệu" : price.ToString("N0", Vi) + " đ";
        private static string FinalGuard(string msg, Scenario sc, List<Property> props, PageInfo page) { msg = Clean(msg); if (Unsafe(Normalize(msg))) return UnsafeAnswer(); if ((sc.Name is "Buy" or "Rent") && !props.Any() && ContainsAny(Normalize(msg), "em tim thay", "em tim duoc", "tin phu hop ben duoi")) return "Em chưa đủ dữ liệu hoặc chưa có kết quả SQL phù hợp nên không đề xuất tin. Anh/Chị vui lòng bổ sung loại BĐS, khu vực và ngân sách."; return msg.Length > 6500 ? Trim(msg, 6500) + "..." : msg; }
        private static string Clean(string s) => Regex.Replace((s ?? "").Replace("```", "").Trim(), @"\n{4,}", "\n\n");

        private static void ApplyShortAnswer(string n, Dictionary<string, string> slots, List<AIChatMessage> history)
        {
            string last = Normalize(history.LastOrDefault(x => x.Role == "assistant")?.Content ?? "");

            // Bổ sung tiêu chí sau câu hỏi dẫn dắt: ngân sách, mục đích, pháp lý.
            if (ContainsAny(last, "ngan sach", "bao nhieu", "gia"))
                ExtractMoney(n, slots, new(SlotIs(slots, "deal_type", "Thuê") ? "Rent" : "Buy", "", true, true, false));

            string? purpose = Purpose(n);
            if (purpose != null) slots["purpose"] = purpose;

            string? legal = LegalNeed(n);
            if (legal != null) slots["legal_requirement"] = legal;

            string? road = RoadNeed(n);
            if (road != null) slots["road_requirement"] = road;

            string? amenities = Amenities(n);
            if (amenities != null) slots["amenities"] = amenities;
        }
        private static void ExtractMoney(string n, Dictionary<string, string> slots, Scenario sc)
        {
            bool rent = sc.Name == "Rent" || ContainsAny(n, "/thang", "moi thang", "gia thue");

            (decimal Min, decimal Max)? range = MoneyRange(n);
            if (range.HasValue && !rent)
            {
                slots["budget_min"] = range.Value.Min.ToString(CultureInfo.InvariantCulture);
                slots["budget_max"] = range.Value.Max.ToString(CultureInfo.InvariantCulture);
                return;
            }

            List<decimal> vals = MoneyValues(n);
            if (!vals.Any()) return;

            bool lower = ContainsAny(n, "tren", "tu", "it nhat", "toi thieu");
            bool upper = ContainsAny(n, "duoi", "toi da", "khong qua");

            if (rent)
            {
                slots["rent_max"] = vals.Max().ToString(CultureInfo.InvariantCulture);
                return;
            }

            if (vals.Count >= 2 && ContainsAny(n, "tu", "den", "toi", "khoang"))
            {
                slots["budget_min"] = vals.Min().ToString(CultureInfo.InvariantCulture);
                slots["budget_max"] = vals.Max().ToString(CultureInfo.InvariantCulture);
                return;
            }

            if (lower && !upper) slots["budget_min"] = vals.Max().ToString(CultureInfo.InvariantCulture);
            else slots["budget_max"] = vals.Max().ToString(CultureInfo.InvariantCulture);
        }

        private static (decimal Min, decimal Max)? MoneyRange(string n)
        {
            Match m = Regex.Match(n, @"(?<a>\d+(?:[\.,]\d+)?)\s*(?:-|den|toi)\s*(?<b>\d+(?:[\.,]\d+)?)\s*(?<unit>ty|ti|t\b|trieu|tr|cu)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (!decimal.TryParse(m.Groups["a"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal a)) return null;
            if (!decimal.TryParse(m.Groups["b"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal b)) return null;
            string unit = m.Groups["unit"].Value.ToLowerInvariant();
            decimal factor = unit is "ty" or "ti" or "t" ? 1_000_000_000m : 1_000_000m;
            return (Math.Min(a, b) * factor, Math.Max(a, b) * factor);
        }

        private static List<decimal> MoneyValues(string n)
        {
            List<decimal> res = new();
            foreach (Match m in Regex.Matches(n, @"(?<a>\d+)\s*(?:ty|ti|t)\s*(?<b>\d)\b"))
                if (decimal.TryParse(m.Groups["a"].Value, out decimal a) && decimal.TryParse(m.Groups["b"].Value, out decimal b))
                    res.Add((a + b / 10m) * 1_000_000_000m);

            foreach (Match m in Regex.Matches(n, @"(?<num>\d+(?:[\.,]\d+)?)\s*(?<unit>ty|ti|t\b|trieu|tr|cu|k)", RegexOptions.IgnoreCase))
            {
                if (!decimal.TryParse(m.Groups["num"].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal num)) continue;
                string u = m.Groups["unit"].Value.ToLowerInvariant();
                res.Add(u is "ty" or "ti" or "t" ? num * 1_000_000_000m : u is "trieu" or "tr" or "cu" ? num * 1_000_000m : num * 1000m);
            }
            return res.Where(x => x > 0).Distinct().ToList();
        }

        private static void ExtractAreaSize(string n, Dictionary<string, string> slots) { var m = Regex.Match(n, @"(?<min>\d+(?:[\.,]\d+)?)\s*(?:m2|met|m)\s*(?:den|toi|-)\s*(?<max>\d+(?:[\.,]\d+)?)"); if (m.Success) { slots["area_min"] = Parse(m.Groups["min"].Value).ToString(CultureInfo.InvariantCulture); slots["area_max"] = Parse(m.Groups["max"].Value).ToString(CultureInfo.InvariantCulture); return; } var a = Regex.Match(n, @"(?<num>\d+(?:[\.,]\d+)?)\s*(?:m2|met vuong|met|m\b)"); if (a.Success) { decimal v = Parse(a.Groups["num"].Value); slots["area_min"] = Math.Max(1, v * .85m).ToString("0.##", CultureInfo.InvariantCulture); slots["area_max"] = (v * 1.15m).ToString("0.##", CultureInfo.InvariantCulture); } }
        private static decimal Parse(string s) { decimal.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v); return v; }
        private static decimal? DecimalSlot(Dictionary<string, string> slots, string key) => slots.TryGetValue(key, out var s) && decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
        private static bool HasBudget(Dictionary<string, string> slots) => slots.ContainsKey("budget_max") || slots.ContainsKey("budget_min") || slots.ContainsKey("rent_max");

        private static string? PropertyType(string n)
        {
            if (ContainsAny(n, "can ho", "chung cu")) return "Căn hộ";
            if (ContainsAny(n, "phong tro", "nha tro")) return "Phòng trọ";

            // Không dùng riêng từ "kinh doanh" để đổi loại hình thành Mặt bằng.
            // "kinh doanh" là MỤC ĐÍCH; chỉ khi có cụm rõ như mặt bằng/văn phòng/cửa hàng/shop... mới là loại hình.
            if (ContainsAny(n, "mat bang", "mat bang kinh doanh", "van phong", "cua hang", "shop", "shophouse", "showroom", "toa nha", "nha pho thuong mai"))
                return "Mặt bằng";

            if (ContainsAny(n, "biet thu", "villa")) return "Biệt thự";
            if (ContainsAny(n, "nha rieng", "nha pho", "nha nguyen can", "mua nha", "can nha", "co nha", "nha nao", "tim nha")) return "Nhà";
            if (ContainsAny(n, "dat", "lo dat", "dat nen", "dat tho cu")) return "Đất";
            return null;
        }

        private static string? AreaName(string n)
        {
            string normalized = Normalize(n);

            foreach (var kv in AreaMap())
            {
                string key = Normalize(kv.Key);

                // Alias cực ngắn như "nt" chỉ nhận khi là một từ riêng biệt.
                // Không dùng ContainsCompact cho alias ngắn vì dễ khớp bậy trong các chữ khác.
                if (key.Length <= 3)
                {
                    if (Regex.IsMatch(normalized, $@"(^|\s){Regex.Escape(key)}($|\s)"))
                        return kv.Value;
                    continue;
                }

                if (ContainsCompact(normalized, key)) return kv.Value;
            }
            return null;
        }

        private static Dictionary<string, string> AreaMap() => new(StringComparer.OrdinalIgnoreCase)
        {
            // Khánh Hòa cũ
            ["nha trang"] = "Nha Trang",
            ["nhatrang"] = "Nha Trang",
            ["nt"] = "Nha Trang",
            ["cam lam"] = "Cam Lâm",
            ["camlam"] = "Cam Lâm",
            ["cam ranh"] = "Cam Ranh",
            ["camranh"] = "Cam Ranh",
            ["ninh hoa"] = "Ninh Hòa",
            ["ninhhoa"] = "Ninh Hòa",
            ["dien khanh"] = "Diên Khánh",
            ["dienkhanh"] = "Diên Khánh",
            ["van ninh"] = "Vạn Ninh",
            ["vanninh"] = "Vạn Ninh",
            ["khanh vinh"] = "Khánh Vĩnh",
            ["khanhvinh"] = "Khánh Vĩnh",
            ["khanh son"] = "Khánh Sơn",
            ["khanhson"] = "Khánh Sơn",
            ["khanh hoa"] = "Khánh Hòa",

            // Khu vực Ninh Thuận cũ trong phạm vi Khánh Hòa mới theo dữ liệu đồ án
            ["phan rang"] = "Phan Rang-Tháp Chàm",
            ["phan rang thap cham"] = "Phan Rang-Tháp Chàm",
            ["prtc"] = "Phan Rang-Tháp Chàm",
            ["ninh hai"] = "Ninh Hải",
            ["ninhhai"] = "Ninh Hải",
            ["ninh phuoc"] = "Ninh Phước",
            ["ninhphuoc"] = "Ninh Phước",
            ["thuan bac"] = "Thuận Bắc",
            ["thuanbac"] = "Thuận Bắc",
            ["thuan nam"] = "Thuận Nam",
            ["thuannam"] = "Thuận Nam",
            ["ninh son"] = "Ninh Sơn",
            ["ninhson"] = "Ninh Sơn",
            ["bac ai"] = "Bác Ái",
            ["bacai"] = "Bác Ái",
            ["ninh chu"] = "Ninh Chữ",
            ["ninhchu"] = "Ninh Chữ",
            ["vinh hy"] = "Vĩnh Hy",
            ["vinhhy"] = "Vĩnh Hy",
            ["ca na"] = "Cà Ná",
            ["cana"] = "Cà Ná"
        };

        private static List<string> AreaAliases(string area)
        {
            string n = Normalize(area);
            List<string> aliases = new() { n };

            foreach (var kv in AreaMap())
            {
                string key = Normalize(kv.Key);
                string value = Normalize(kv.Value);

                bool isSameArea = ContainsCompact(n, key) || ContainsCompact(n, value);
                if (!isSameArea) continue;

                // Không thêm alias quá ngắn kiểu "nt" vào danh sách khớp khi người dùng nói "Nha Trang".
                // Nếu thêm "nt", rất dễ khớp bậy với chữ trong địa chỉ/mô tả và lôi tin Diên Khánh/Cam Lâm/Ninh Phước ra.
                if (key.Length >= 4 || n == key) aliases.Add(key);
                if (value.Length >= 4) aliases.Add(value);
            }

            if (ContainsAny(n, "phan rang", "phan rang thap cham", "prtc"))
                aliases.AddRange(new[] { "phan rang", "phan rang thap cham", "thap cham", "phuoc my", "dao long", "my hai", "my dong", "bao an", "kinh dinh" });

            if (ContainsAny(n, "ninh hai", "ninh chu", "vinh hy"))
                aliases.AddRange(new[] { "ninh hai", "ninh chu", "vinh hy", "khanh hai", "tri hai", "nhon hai", "thanh hai" });

            if (ContainsAny(n, "ninh phuoc"))
                aliases.AddRange(new[] { "ninh phuoc", "phuoc dan", "phuoc hai", "phuoc huu", "phuoc thuan", "phuoc son" });

            if (ContainsAny(n, "thuan nam", "ca na"))
                aliases.AddRange(new[] { "thuan nam", "ca na", "phuoc diem", "phuoc nam", "son hai" });

            if (ContainsAny(n, "thuan bac")) aliases.AddRange(new[] { "thuan bac", "loi hai", "cong hai", "phuoc chien" });
            if (ContainsAny(n, "ninh son")) aliases.AddRange(new[] { "ninh son", "tan son", "lam son", "quang son" });
            if (ContainsAny(n, "bac ai")) aliases.AddRange(new[] { "bac ai", "phuoc dai", "phuoc tan", "phuoc tien" });

            return aliases
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(Normalize)
                .Where(x => x.Length >= 4 || x == n)
                .Distinct()
                .ToList();
        }

        private static bool IsProvinceWideArea(string areaNeed)
        {
            return ContainsAny(areaNeed, "khanh hoa", "toan tinh", "toan khanh hoa", "tinh khanh hoa", "khanh hoa moi");
        }
        private static string? Purpose(string n)
        {
            bool live = ContainsAny(n, "o thuc", "de o", "xay nha", "gia dinh", "o lau dai");
            bool business = ContainsAny(n, "kinh doanh", "mo quan", "buon ban", "lam mat bang", "vua o vua kinh doanh");
            if (live && business) return "Ở kết hợp kinh doanh";
            if (live) return "Ở thực";
            if (ContainsAny(n, "dau tu", "giu tai san", "ban lai")) return "Đầu tư";
            if (business) return "Kinh doanh";
            return null;
        }
        private static string? LegalNeed(string n)
        {
            if (ContainsAny(n, "so rieng", "so do", "so hong", "phap ly ro", "co so", "co so day du", "so day du", "so ro rang", "giay to day du", "phap ly day du"))
                return "Ưu tiên sổ riêng/pháp lý rõ";
            if (ContainsAny(n, "tho cu", "dat o")) return "Ưu tiên đất ở/thổ cư";
            return null;
        }
        private static string? RoadNeed(string n) { if (ContainsAny(n, "o to", "oto", "xe hoi")) return "Đường ô tô"; if (ContainsAny(n, "mat tien")) return "Mặt tiền"; return null; }
        private static string? Amenities(string n) { List<string> a = new(); if (ContainsAny(n, "gan cho")) a.Add("gần chợ"); if (ContainsAny(n, "gan truong")) a.Add("gần trường"); if (ContainsAny(n, "gan bien")) a.Add("gần biển"); if (ContainsAny(n, "trung tam")) a.Add("gần trung tâm"); return a.Any() ? string.Join(", ", a) : null; }

        private static string? PageField(PageInfo page, string label)
        {
            if (string.IsNullOrWhiteSpace(page.ContextText) || string.IsNullOrWhiteSpace(label)) return null;

            string text = Regex.Replace(page.ContextText, @"\s+", " ").Trim();
            string[] labels =
            {
                "Tiêu đề", "Tiêu đề tin", "Loại giao dịch", "Loại bất động sản", "Loại BĐS",
                "Giá", "Diện tích", "Đơn giá", "Vị trí", "Khu vực", "Phường/Xã",
                "Dự án liên quan", "Phòng ngủ", "Phòng tắm", "Hướng nhà", "Pháp lý",
                "Tiện ích", "Tình trạng tin", "Ngày đăng", "Người đăng", "Chủ tin",
                "Người bán", "Môi giới", "Mô tả người đăng"
            };

            string lookAhead = string.Join("|", labels.Select(Regex.Escape));
            string pattern = Regex.Escape(label) + @"\s*:\s*(?<v>.*?)(?=\s+(?:" + lookAhead + @")\s*:|$)";
            Match m = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;

            string value = Regex.Replace(m.Groups["v"].Value, @"\s+", " ").Trim();
            value = value.Trim(' ', '.', ';', '|');

            // Sửa các trường bị rút cụt do HTML hoặc dấu câu lạ.
            if (label.Equals("Giá", StringComparison.OrdinalIgnoreCase))
            {
                Match price = Regex.Match(value, @"\d+(?:[\.,]\d+)?\s*(?:tỷ|ty|triệu|tr|đ|dong)?", RegexOptions.IgnoreCase);
                if (price.Success) value = NormalizeDisplayMoney(price.Value, value);
            }
            if (label.Equals("Diện tích", StringComparison.OrdinalIgnoreCase))
            {
                Match area = Regex.Match(value, @"\d+(?:[\.,]\d+)?\s*(?:m²|m2|m|met vuong)?", RegexOptions.IgnoreCase);
                if (area.Success) value = area.Value.Contains("m") ? area.Value : area.Value + " m²";
            }

            return string.IsNullOrWhiteSpace(value) ? null : Trim(value, 350);
        }

        private static string CleanTitle(string? title, string fallback)
        {
            if (string.IsNullOrWhiteSpace(title)) return fallback;
            string t = title.Replace("- BDSKhanhHoa", "", StringComparison.OrdinalIgnoreCase).Trim();
            return string.IsNullOrWhiteSpace(t) ? fallback : t;
        }

        private static decimal? FirstMoneyAfter(string n, params string[] anchors)
        {
            string normalized = Normalize(n);
            foreach (string anchor in anchors)
            {
                int idx = normalized.IndexOf(Normalize(anchor), StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string part = normalized[idx..];
                List<decimal> values = MoneyValues(part);
                if (values.Any()) return values.First();
            }
            return null;
        }

        private static PageInfo BuildPageInfo(ChatRequest req) { string type = string.IsNullOrWhiteSpace(req.PageType) ? "General" : req.PageType.Trim(); string url = req.PageUrl ?? ""; string title = req.PageTitle ?? ""; string ctx = StripHtml(req.PageContext ?? ""); string u = Normalize(url); if (type == "General" && u.Contains("/property/details")) type = "PropertyDetail"; if (type == "General" && u.Contains("/project/details")) type = "ProjectDetail"; return new PageInfo { PageType = type, PageUrl = url, PageTitle = title, ContextText = ctx }; }
        private static string StripHtml(string s) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<.*?>", " ")), @"\s+", " ").Trim();
        private static Dictionary<string, string> CleanSlots(Dictionary<string, string> slots) => slots.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary(x => x.Key, x => Trim(x.Value, 1000), StringComparer.OrdinalIgnoreCase);
        private static string Trim(string s, int max) => string.IsNullOrWhiteSpace(s) ? "" : s.Length <= max ? s : s[..max];
        private static string Normalize(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            string t = input.Trim().ToLowerInvariant();

            // Giữ số thập phân tiếng Việt: 1,5 tỷ -> 1.5 ty; 8,81 tỷ -> 8.81 ty.
            t = Regex.Replace(t, @"(?<=\d),(?=\d)", ".");

            t = t.Replace('đ', 'd').Normalize(NormalizationForm.FormD);
            StringBuilder sb = new();
            foreach (char c in t)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            t = sb.ToString().Normalize(NormalizationForm.FormC);

            var map = new Dictionary<string, string>
            {
                { "bds", "bat dong san" }, { "sodo", "so do" }, { "phap li", "phap ly" }, { "phaply", "phap ly" },
                { "nhatrang", "nha trang" }, { "nha trag", "nha trang" }, { "ntrang", "nha trang" },
                { "dienkhanh", "dien khanh" }, { "camlam", "cam lam" }, { "camranh", "cam ranh" },
                { "ninhhoa", "ninh hoa" }, { "vanninh", "van ninh" }, { "khanhvinh", "khanh vinh" }, { "khanhson", "khanh son" },
                { "phanrang", "phan rang" }, { "phan rang-thap cham", "phan rang thap cham" }, { "phanrangthapcham", "phan rang thap cham" },
                { "ninhhai", "ninh hai" }, { "ninhphuoc", "ninh phuoc" }, { "thuanbac", "thuan bac" }, { "thuannam", "thuan nam" },
                { "ninhson", "ninh son" }, { "bacai", "bac ai" }, { "ninhchu", "ninh chu" }, { "vinhhy", "vinh hy" }, { "cana", "ca na" },
                { "1ty5", "1.5 ty" }, { "1t5", "1.5 ty" }, { "2ty", "2 ty" }, { "3ty", "3 ty" }, { "4ty", "4 ty" }, { "5ty", "5 ty" },
                { "500tr", "500 trieu" }, { "800tr", "800 trieu" }, { "7tr", "7 trieu" }, { "8tr", "8 trieu" },
                { "oto", "o to" }, { "xe hoi", "o to" }, { "ko", "khong" }, { "k", "khong" },
                { "thang", "thang" }
            };

            foreach (var kv in map) t = Regex.Replace(t, $@"\b{Regex.Escape(kv.Key)}\b", kv.Value);
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*tỷ", "${num} ty");
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*tỉ", "${num} ti");
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*triệu", "${num} trieu");
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*tr/tháng", "${num} trieu thang");
            t = Regex.Replace(t, @"(?<num>\d+(?:[\.,]\d+)?)\s*tr/thang", "${num} trieu thang");

            t = Regex.Replace(t, @"[^a-z0-9\s\.,/\-<>=%]+", " ");
            return Regex.Replace(t, @"\s+", " ").Trim();
        }
        private static bool ContainsAny(string text, params string[] keys) { string n = Normalize(text); string c = Regex.Replace(n, @"\s+", ""); foreach (var k in keys) { string nk = Normalize(k); if (n.Contains(nk)) return true; string ck = Regex.Replace(nk, @"\s+", ""); if (ck.Length >= 3 && c.Contains(ck)) return true; } return false; }
        private static bool ContainsCompact(string text, string key) { string a = Regex.Replace(Normalize(text), @"\s+", ""); string b = Regex.Replace(Normalize(key), @"\s+", ""); return !string.IsNullOrWhiteSpace(b) && (a.Contains(b) || b.Contains(a)); }
        private static bool SlotIs(Dictionary<string, string> slots, string k, string v) => slots.TryGetValue(k, out var cur) && cur.Contains(v, StringComparison.OrdinalIgnoreCase);

        private static bool IsSearchNeedContinuation(string n, AIChatSession session, Dictionary<string, string> slots)
        {
            bool activeSearchScenario = session.Scenario == "Buy" || session.Scenario == "Rent" ||
                                        SlotIs(slots, "deal_type", "Mua") || SlotIs(slots, "deal_type", "Thuê");
            if (!activeSearchScenario) return false;

            // Nếu người dùng chuyển hẳn sang pháp lý/vay/đăng tin/hỗ trợ thì không coi là bổ sung tiêu chí tìm tin.
            if (StandaloneLoan(n) || StandaloneLegal(n) || StandaloneTransaction(n) ||
                StandalonePosting(n) || StandaloneProject(n) || Complaint(n) || Appointment(n) || Support(n))
                return false;

            bool givesBudget = MoneyValues(n).Any() || MoneyRange(n).HasValue || ContainsAny(n, "duoi", "tren", "tam", "khoang", "ngan sach", "gia");
            bool givesPurpose = Purpose(n) != null;
            bool givesLegal = LegalNeed(n) != null;
            bool givesType = PropertyType(n) != null;
            bool givesArea = AreaName(n) != null;
            bool givesRoad = RoadNeed(n) != null;
            bool givesAmenity = Amenities(n) != null;
            bool givesAreaSize = Regex.IsMatch(n, @"\d+(?:[\.,]\d+)?\s*(m2|met|m\b)", RegexOptions.IgnoreCase);

            return givesBudget || givesPurpose || givesLegal || givesType || givesArea || givesRoad || givesAmenity || givesAreaSize || Refinement(n) || Another(n);
        }

        private static bool IsMultiIntentQuestion(string n)
        {
            int count = 0;
            if (HasPropertyNeedIntent(n)) count++;
            if (Legal(n)) count++;
            if (Loan(n)) count++;
            if (Transaction(n)) count++;
            if (Posting(n)) count++;
            if (Project(n)) count++;
            if (Market(n)) count++;
            return count >= 2 && ContainsAny(n, "va", "và", "luon", "cung", "dong thoi", "nhung", "hoi");
        }

        private static bool ExplicitListing(string n) => ContainsAny(n, "tim tin", "loc tin", "goi y tin", "de xuat tin", "cho toi xem", "co tin nao", "co dat nao", "co nha nao", "toi muon tim", "muon tim", "tim 1", "tim mot", "tim mat bang", "tim nha", "tim dat") || ((Buy(n) || Rent(n) || Search(n)) && PropertyType(n) != null && AreaName(n) != null && (MoneyValues(n).Any() || MoneyRange(n).HasValue || ContainsAny(n, "duoi", "tren", "ngan sach", "gia", "khoang")));
        private static bool BuySafety(string n) => ContainsAny(n, "khong biet mua nhu nao cho an toan", "mua nhu nao cho an toan", "mua dat an toan", "can kiem tra gi khi mua", "lan dau mua dat", "so bi lua", "tranh bi lua", "kinh nghiem mua dat");
        private static bool Buy(string n) => (ContainsAny(n, "toi muon mua", "can mua", "muon mua", "tim mua", "mua dat", "mua nha", "mua can ho", "mua mat bang", "mua bds", "dat xay nha", "lo dat", "dat nen", "co nha nao", "co dat nao", "nha nao") || (ContainsAny(n, "mua") && PropertyType(n) != null)) && !ContainsAny(n, "vay ngan hang", "thu nhap", "lai suat", "hop dong thue", "can thue", "cho thue", "muon thue");
        private static bool Rent(string n) => ContainsAny(n, "toi muon thue", "can thue", "muon thue", "tim thue", "thue nha", "thue can ho", "thue phong tro", "thue mat bang") && !ContainsAny(n, "toi muon ban");
        private static bool Search(string n) => ContainsAny(n, "loc tin", "tim tin", "xem tin", "goi y tin", "co tin nao", "danh sach tin", "toi muon tim", "muon tim", "tim 1", "tim mot", "tim mat bang", "tim nha", "tim dat", "tim can ho");
        private static bool Another(string n) => ContainsAny(n, "tin nao khac", "xem them", "tim them", "gia thap hon", "gan trung tam hon", "dien tich lon hon", "phap ly ro hon");
        private static bool Refinement(string n) => ContainsAny(n, "gia thap hon", "re hon", "gan trung tam hon", "dien tich lon hon", "phap ly ro", "duong o to", "mat tien", "gan bien");
        private static bool Appointment(string n) => ContainsAny(n, "dat lich", "lich hen", "hen xem", "dat lich xem", "muon hen xem");
        private static bool CurrentProperty(string n) => ContainsAny(n, "tin nay", "nha nay", "dat nay", "lo nay", "bds nay", "bat dong san nay", "gia nay", "vi tri nay", "khu vuc nao", "o dau", "ai la chu", "chu tin", "nguoi dang", "uy tin", "co nen mua", "co nen dat coc", "rui ro", "dang xem", "ban lai", "thanh khoan", "xay nha", "cat nha", "de o", "phap ly nay", "bao cao tin nay");
        private static bool Legal(string n) => ContainsAny(n, "phap ly", "so do", "so hong", "quy hoach", "lo gioi", "the chap", "tranh chap", "giay tay", "kiem tra gi", "anh chup so");
        private static bool Transaction(string n) => ContainsAny(n, "quy trinh", "thu tuc", "sang ten", "cong chung", "dat coc", "thanh toan", "chuyen het tien", "thue thu nhap", "le phi truoc ba", "chi phi", "thue khi ban lai", "ban lai", "phi cong chung");
        private static bool Loan(string n) => ContainsAny(n, "vay ngan hang", "vay mua", "tra gop", "lai suat", "thu nhap", "luong", "ngan hang co cho vay", "tham dinh", "no xau", "ap luc", "vay duoc bao nhieu", "nen vay", "vay 50", "vay 70", "vay 1 ty", "trong 20 nam", "dung het tien tiet kiem", "ho so vay", "vay xay nha");
        private static bool Posting(string n) => ContainsAny(n, "dang tin", "viet tieu de", "viet mo ta", "soan tin", "toi muon ban", "tin dang bi tu choi", "dinh gia", "ban nhanh", "ep gia", "trinh bay tin", "de co khach", "bao loi", "chac chan tang gia", "bao lai");
        private static bool Project(string n) => ContainsAny(n, "du an", "chu dau tu", "hinh thanh trong tuong lai", "bao lanh ngan hang", "cham tien do", "cam ket loi nhuan");
        private static bool Support(string n) => ContainsAny(n, "tai khoan", "thanh toan roi", "chua duoc cong luot", "hotline", "lien he admin", "ho tro");
        private static bool Complaint(string n) => ContainsAny(n, "khieu nai", "lua dao", "tin gia", "tin sai", "bao cao tin", "bao cao", "vi pham", "report tin", "tin lua dao");
        private static bool Market(string n) => ContainsAny(n, "nen mua", "so sanh", "dat nen hay nha", "gia re bat thuong", "uu tien phap ly hay vi tri", "co dang nghi", "ban lai", "thanh khoan", "giu tai san", "de ban lai", "ngan sach gioi han", "giam dien tich", "giam vi tri", "dat mat tien gia cao", "mat tien gia cao", "duong nho", "hem nho");
        private static bool StandaloneLegal(string n) => (Legal(n) || BuildPermissionQuestion(n)) && (ContainsAny(n, "quy hoach", "giay tay", "anh chup so", "so do va so hong", "kiem tra", "duoc xay", "xay nha", "muc dich su dung dat") || n.Length < 140);
        private static bool StandaloneTransaction(string n) => Transaction(n) && ContainsAny(n, "quy trinh", "sang ten", "cong chung", "dat coc", "chuyen het tien");
        private static bool StandaloneLoan(string n) => Loan(n) && (ContainsAny(n, "thu nhap", "luong", "vay duoc bao nhieu", "lai suat", "ngan hang co cho vay", "ho so vay", "ap luc", "nen vay", "vay 50", "vay 70", "vay 1 ty", "trong 20 nam", "dung het tien tiet kiem", "vay xay nha") || (ContainsAny(n, "mua nha", "mua dat") && ContainsAny(n, "luong", "thu nhap", "vay", "ap luc")));
        private static bool StandalonePosting(string n) => Posting(n) && ContainsAny(n, "huong dan", "viet", "soan", "bi tu choi", "ban nhanh", "dinh gia", "ep gia", "trinh bay", "de co khach", "bao loi", "chac chan tang gia");
        private static bool StandaloneProject(string n) => Project(n) && ContainsAny(n, "kiem tra", "rui ro", "bao lanh", "cam ket loi nhuan", "chu dau tu");
        private static bool StandaloneMarket(string n) => (Market(n) || FeasibilityAdviceQuestion(n)) && ContainsAny(n, "so sanh", "nen mua", "co dang nghi", "uu tien", "ban lai", "thanh khoan", "giu tai san", "nen chon", "kha thi", "co on khong", "hoi nguoi ban", "ngan sach gioi han", "giam dien tich", "giam vi tri", "dat mat tien", "duong nho", "hem nho");
        private static bool Unsafe(string n) => ContainsAny(n, "lam gia so do", "lam gia so hong", "hop dong gia", "ne thue", "tron thue", "lach thue", "giau tranh chap", "che giau tranh chap", "hack", "lua nguoi mua", "lua nguoi ban", "noi qua len de lua", "viet tin gian doi", "ca do");
        private static bool OffTopic(string n) { bool bds = ContainsAny(n, "bat dong san", "bds", "nha", "dat", "can ho", "mat bang", "kinh doanh", "thue", "mua", "ban", "du an", "so do", "phap ly", "nha trang", "khanh hoa", "khanh vinh", "phan rang", "ninh thuan", "ninh hai", "ninh phuoc", "thuan nam", "thuan bac", "ninh son", "bac ai"); return !bds && ContainsAny(n, "hom nay an gi", "bong da", "game", "phim", "tinh yeu", "may ngu", "ngu qua"); }
        private static List<string> RepliesForRoute(Route r, string s) => r == Route.Clarify ? new List<string> { "Mua để ở", "Mua đầu tư", "Dưới 1 tỷ", "Cần sổ riêng" } : Replies(s);
        private static List<string> Replies(string s) => s switch { "Buy" => new() { "Lọc giá thấp hơn", "Ưu tiên pháp lý rõ", "Mở rộng khu vực", "Mua đất cần kiểm tra gì?" }, "Rent" => new() { "Lọc dưới ngân sách", "Gần trung tâm", "Có nội thất", "Hợp đồng thuê cần lưu ý gì?" }, "Legal" => new() { "Mua đất cần giấy tờ gì?", "Kiểm tra quy hoạch ở đâu?", "Có nên đặt cọc không?" }, _ => new() { "Tôi muốn mua đất", "Tôi muốn thuê nhà", "Mua đất cần kiểm tra gì?", "Hướng dẫn đăng tin" } };

        private enum Route { Clarify, Search, PageAnalysis, Direct, AI, Refuse, OffTopic }
        private sealed class Scenario { public Scenario(string name, string intent, bool guide, bool search, bool needHuman) { Name = name; Intent = intent; ShouldGuide = guide; ShouldSearch = search; NeedHuman = needHuman; } public string Name { get; } public string Intent { get; } public bool ShouldGuide { get; } public bool ShouldSearch { get; } public bool NeedHuman { get; } }
        private sealed class SlotPlan { public List<string> Missing { get; set; } = new(); }
        private sealed class PageInfo { public string PageType { get; set; } = "General"; public string PageUrl { get; set; } = ""; public string PageTitle { get; set; } = ""; public string ContextText { get; set; } = ""; public bool IsPropertyDetail => PageType == "PropertyDetail"; public bool IsProjectDetail => PageType == "ProjectDetail"; public bool HasUsefulContext => !string.IsNullOrWhiteSpace(ContextText) && ContainsAny(ContextText, "gia", "dien tich", "vi tri", "phap ly", "loai", "tieu de"); }
    }
}

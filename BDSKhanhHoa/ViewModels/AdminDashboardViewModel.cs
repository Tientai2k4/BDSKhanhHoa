namespace BDSKhanhHoa.Areas.Admin.ViewModels
{
    public class AdminDashboardViewModel
    {
        public decimal TotalRevenue { get; set; }
        public decimal YearRevenue { get; set; }
        public decimal MonthlyRevenue { get; set; }
        public decimal TodayRevenue { get; set; }

        public int TotalProperties { get; set; }
        public int PendingProperties { get; set; }
        public int ApprovedProperties { get; set; }
        public int SoldProperties { get; set; }
        public int RentedProperties { get; set; }

        public int TotalProjects { get; set; }
        public int PendingProjects { get; set; }

        public int TotalUsers { get; set; }
        public int NewUsersThisMonth { get; set; }

        public int PendingReports { get; set; }
        public int TotalChatInteractions { get; set; }
        public int ChatInteractionsToday { get; set; }

        public int TotalTransactions { get; set; }
        public int SuccessfulTransactions { get; set; }
        public int PendingTransactions { get; set; }
        public int FailedTransactions { get; set; }

        public int TotalAppointments { get; set; }
        public int PendingAppointments { get; set; }

        public int TotalConsultations { get; set; }
        public int PendingConsultations { get; set; }

        public int TotalNeedProcess =>
            PendingProperties + PendingProjects + PendingReports + PendingTransactions + PendingAppointments + PendingConsultations;

        public int SelectedYear { get; set; }
        public List<int> AvailableYears { get; set; } = new();

        public List<string> MonthLabels { get; set; } = new();

        public List<decimal> RevenueData { get; set; } = new();
        public List<int> PropertyCreatedData { get; set; } = new();
        public List<int> PropertySoldData { get; set; } = new();
        public List<int> PropertyRentedData { get; set; } = new();
        public List<int> UserData { get; set; } = new();
        public List<int> ChatData { get; set; } = new();
        public List<int> TransactionData { get; set; } = new();
        public List<int> AuditLogData { get; set; } = new();

        public List<PackageRevenueItem> PackageRevenueItems { get; set; } = new();
        public List<StatusStatisticItem> PropertyStatusItems { get; set; } = new();
        public List<StatusStatisticItem> TransactionStatusItems { get; set; } = new();
        public List<RecentTransactionItem> RecentTransactions { get; set; } = new();
    }

    public class PackageRevenueItem
    {
        public string PackageName { get; set; } = "Không rõ";
        public int TotalBuy { get; set; }
        public decimal TotalRevenue { get; set; }
    }

    public class StatusStatisticItem
    {
        public string Status { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Count { get; set; }
    }

    public class RecentTransactionItem
    {
        public int TransactionID { get; set; }
        public string? TransactionCode { get; set; }
        public string UserName { get; set; } = "Không rõ";
        public string? UserAvatar { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public string? Status { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}